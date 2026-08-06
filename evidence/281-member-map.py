#!/usr/bin/env python3
"""Member map for Issue #281: byte-exact segmentation of a C# class body.

Reads a C# file whose *only* top-level type is the class of interest, splits the
class body into members at brace depth 1, and reports for each member: name,
kind, line range, byte length, and which sibling members it names in its body.

Coverage is byte-exact by construction: the file is partitioned into
header + [member spans, each including the trivia that follows it] + footer, so
header + sum(members) + footer == len(file bytes). The script asserts this.

Usage:
    python evidence/281-member-map.py \
        src/DungeonFortress.Simulation/PrototypeWorld.cs PrototypeWorld \
        src/DungeonFortress.Game/Main.cs Main \
        > evidence/281-member-map.json

    python evidence/281-member-map.py cohesion \
        evidence/281-member-map.json evidence/281-split-verification.json \
        > evidence/281-cohesion.json

The `cohesion` subcommand answers one question about a chosen cut: how much of
the class's own call graph it keeps inside a file, next to what an arithmetic
cut would keep and what the best possible cut could keep. It takes the chosen
boundaries from the split verification report rather than from a table of its
own, so the numbers cannot drift away from the files that were actually written.
"""

from __future__ import annotations

import collections
import json
import re
import sys


def code_mask(text: str) -> list[bool]:
    """True where the character is real code (not inside a string/char/comment)."""
    mask = [True] * len(text)
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            j = n if j < 0 else j
            for k in range(i, j):
                mask[k] = False
            i = j
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                mask[k] = False
            i = j
            continue
        # raw string literals: """ (optionally $-prefixed, any number of quotes >= 3)
        m = re.match(r'\$*"{3,}', text[i:])
        if m:
            quote_run = m.group(0).lstrip("$")
            close = quote_run
            j = text.find(close, i + len(m.group(0)))
            j = n if j < 0 else j + len(close)
            for k in range(i, j):
                mask[k] = False
            i = j
            continue
        # verbatim string: @" or $@" or @$"
        m = re.match(r'(?:@\$?|\$@)"', text[i:])
        if m:
            j = i + len(m.group(0))
            while j < n:
                if text[j] == '"':
                    if j + 1 < n and text[j + 1] == '"':
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            for k in range(i, j):
                mask[k] = False
            i = j
            continue
        # regular string: " or $"
        m = re.match(r'\$?"', text[i:])
        if m:
            j = i + len(m.group(0))
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == '"':
                    j += 1
                    break
                if text[j] == "\n":
                    break
                j += 1
            for k in range(i, j):
                mask[k] = False
            i = j
            continue
        if c == "'":
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == "'":
                    j += 1
                    break
                if text[j] == "\n":
                    break
                j += 1
            for k in range(i, j):
                mask[k] = False
            i = j
            continue
        i += 1
    return mask


def find_class_body(text: str, mask: list[bool], class_name: str) -> tuple[int, int]:
    """Return (index of class opening '{', index of its matching '}')."""
    pat = re.compile(r"\bclass\s+" + re.escape(class_name) + r"\b")
    for m in pat.finditer(text):
        if not mask[m.start()]:
            continue
        i = m.end()
        while i < len(text) and not (mask[i] and text[i] == "{"):
            i += 1
        depth = 0
        j = i
        while j < len(text):
            if mask[j]:
                if text[j] == "{":
                    depth += 1
                elif text[j] == "}":
                    depth -= 1
                    if depth == 0:
                        return i, j
            j += 1
    raise SystemExit(f"class {class_name} not found")


KIND_PATTERNS = [
    ("nested-record", re.compile(r"(?<![@\w])record\s+\w")),
    ("nested-class", re.compile(r"(?<![@\w])class\s+\w")),
    ("nested-struct", re.compile(r"(?<![@\w])struct\s+\w")),
    ("nested-enum", re.compile(r"(?<![@\w])enum\s+\w")),
    ("nested-interface", re.compile(r"(?<![@\w])interface\s+\w")),
    ("delegate", re.compile(r"(?<![@\w])delegate\s+\w")),
    ("event", re.compile(r"(?<![@\w])event\s+\w")),
]

IDENT_RE = re.compile(r"\b([A-Za-z_][A-Za-z0-9_]*)\b")

KEYWORDS = {
    "public", "private", "protected", "internal", "static", "readonly", "const",
    "sealed", "abstract", "virtual", "override", "partial", "async", "extern",
    "unsafe", "new", "ref", "volatile", "required", "get", "set", "init", "this",
}


def declaration_head(decl: str) -> str:
    """Text of the declaration up to its body/initializer, at nesting depth 0."""
    depth = 0
    i = 0
    n = len(decl)
    while i < n:
        c = decl[i]
        if c in "([":
            depth += 1
        elif c in ")]":
            depth -= 1
        elif depth == 0:
            if c == "{" or c == ";":
                return decl[:i]
            if c == "=":
                if decl[i:i + 2] == "=>" or decl[i:i + 2] == "==":
                    return decl[:i]
                if i and decl[i - 1] in "<>!+-*/%&|^":
                    i += 1
                    continue
                return decl[:i]
        i += 1
    return decl


def strip_trivia(decl: str) -> str:
    """Drop leading comments and attributes from a member declaration."""
    out = []
    for line in decl.splitlines():
        s = line.strip()
        if s.startswith("//") or s.startswith("/*") or s.startswith("*") or s.startswith("*/"):
            continue
        if s.startswith("[") and s.endswith("]"):
            continue
        out.append(line)
    return "\n".join(out)


def last_identifier(text: str) -> str:
    names = [m for m in IDENT_RE.findall(text) if m not in KEYWORDS]
    return names[-1] if names else "?"


def has_initializer(body: str, kind: str) -> bool:
    """True when a field/property declaration carries a '= ...' initializer.

    These are the members whose *relative order inside the class* decides the
    order of side effects at construction time, so they are the ones a split
    across files is not allowed to reorder.
    """
    if kind not in ("field", "property"):
        return False
    body = body.strip()
    head = declaration_head(body)
    rest = body[len(head):].lstrip()
    if rest.startswith("=>"):
        return False  # expression-bodied member, not an initializer
    if kind == "field":
        return rest.startswith("=")
    # auto-property with initializer: `{ get; set; } = ...;`
    return bool(re.search(r"\}\s*=(?!=|>)", body)) or rest.startswith("=")


def classify(decl: str, class_name: str) -> tuple[str, str]:
    """Return (kind, name) for a member declaration."""
    body = strip_trivia(decl).strip()
    head = declaration_head(body)
    for kind, pat in KIND_PATTERNS:
        m = pat.search(head)
        if m:
            rest = head[m.start():]
            nm = re.search(
                r"\w+\s+(?:struct\s+|class\s+)?([A-Za-z_][A-Za-z0-9_]*)", rest
            )
            return kind, nm.group(1) if nm else "?"
    if head.rstrip().endswith(")"):
        # parameter list closes the head => method or constructor
        depth = 0
        open_at = None
        for idx in range(len(head.rstrip()) - 1, -1, -1):
            ch = head[idx]
            if ch == ")":
                depth += 1
            elif ch == "(":
                depth -= 1
                if depth == 0:
                    open_at = idx
                    break
        prefix = head[:open_at] if open_at is not None else head
        prefix = re.sub(r"<[^<>]*>\s*$", "", prefix.rstrip())
        name = last_identifier(prefix)
        return ("constructor" if name == class_name else "method"), name
    tail = body[len(head):].lstrip()
    if tail.startswith("{") or tail.startswith("=>"):
        return "property", last_identifier(head)
    return "field", last_identifier(head)


def split_members(text: str, mask: list[bool], open_i: int, close_i: int) -> list[dict]:
    """Partition the class body into members, each span including trailing trivia."""
    members = []
    i = open_i + 1
    while True:
        # skip whitespace to the first real character of the next member
        j = i
        while j < close_i and text[j].isspace():
            j += 1
        if j >= close_i:
            break
        start = j
        depth = 0
        paren = 0
        bracket = 0
        # An expression-bodied member (`... => expr;`) always ends at its ';'.
        # Its expression may legally contain balanced braces — a property
        # pattern (`axis is { } direction`), a collection expression or a
        # statement lambda — so the "brace depth came back to zero" rule must
        # not be applied to it.
        arrow = False
        k = start
        end = None
        while k < close_i:
            if mask[k]:
                c = text[k]
                if c == "(":
                    paren += 1
                elif c == ")":
                    paren -= 1
                elif c == "[":
                    bracket += 1
                elif c == "]":
                    bracket -= 1
                elif (
                    c == "="
                    and text[k:k + 2] == "=>"
                    and depth == 0
                    and paren == 0
                    and bracket == 0
                    and not arrow
                ):
                    arrow = True
                    k += 2
                    continue
                if c == "{":
                    depth += 1
                elif c == "}":
                    depth -= 1
                    if depth == 0 and not arrow:
                        # property with initializer / object initializer: keep going to ';'
                        p = k + 1
                        while p < close_i and (text[p].isspace() or not mask[p]):
                            if not text[p].isspace() and mask[p]:
                                break
                            p += 1
                        if p < close_i and mask[p] and text[p] in "=":
                            k = p
                            continue
                        if p < close_i and mask[p] and text[p] == ";":
                            end = p + 1
                        else:
                            end = k + 1
                        break
                elif c == ";" and depth == 0:
                    end = k + 1
                    break
            k += 1
        if end is None:
            end = close_i
        # attach a same-line trailing comment to this member
        line_end = text.find("\n", end)
        if line_end != -1 and text[end:line_end].strip():
            end = line_end
        members.append({"start": start, "end": end, "decl_end": end})
        i = end
    # extend each member's span to the start of the next member (trailing trivia)
    for idx, mem in enumerate(members):
        mem["span_end"] = members[idx + 1]["start"] if idx + 1 < len(members) else close_i
        mem["span_start"] = mem["start"]
    return members


def line_of(offsets: list[int], pos: int) -> int:
    lo, hi = 0, len(offsets) - 1
    while lo < hi:
        mid = (lo + hi + 1) // 2
        if offsets[mid] <= pos:
            lo = mid
        else:
            hi = mid - 1
    return lo + 1


def analyse(path: str, class_name: str) -> dict:
    raw = open(path, "rb").read()
    text = raw.decode("utf-8")
    mask = code_mask(text)
    open_i, close_i = find_class_body(text, mask, class_name)
    members = split_members(text, mask, open_i, close_i)

    line_starts = [0]
    for idx, ch in enumerate(text):
        if ch == "\n":
            line_starts.append(idx + 1)

    def blen(a: int, b: int) -> int:
        return len(text[a:b].encode("utf-8"))

    entries = []
    for mem in members:
        decl = text[mem["start"]:mem["decl_end"]]
        kind, name = classify(decl, class_name)
        entries.append(
            {
                "name": name,
                "kind": kind,
                "line_start": line_of(line_starts, mem["start"]),
                "line_end": line_of(line_starts, mem["decl_end"] - 1),
                "bytes": blen(mem["span_start"], mem["span_end"]),
                "declaration_bytes": blen(mem["start"], mem["decl_end"]),
                "has_initializer": has_initializer(strip_trivia(decl), kind),
                "static": bool(
                    re.search(r"\bstatic\b", declaration_head(strip_trivia(decl).strip()))
                ),
                "const": bool(
                    re.search(r"\bconst\b", declaration_head(strip_trivia(decl).strip()))
                ),
                "_start": mem["start"],
                "_decl_end": mem["decl_end"],
            }
        )

    names = {e["name"] for e in entries}
    for e in entries:
        body = text[e["_start"]:e["_decl_end"]]
        used = sorted(
            n
            for n in names
            if n != e["name"] and re.search(r"\b" + re.escape(n) + r"\b", body)
        )
        e["references"] = used
        del e["_start"]
        del e["_decl_end"]

    header_bytes = blen(0, members[0]["start"]) if members else blen(0, close_i)
    footer_bytes = blen(members[-1]["span_end"], len(text)) if members else 0
    total = header_bytes + sum(e["bytes"] for e in entries) + footer_bytes
    file_bytes = len(raw)

    return {
        "file": path,
        "class": class_name,
        "file_bytes": file_bytes,
        "file_lines": text.count("\n") + (0 if text.endswith("\n") else 1),
        "header_bytes": header_bytes,
        "footer_bytes": footer_bytes,
        "member_bytes_total": sum(e["bytes"] for e in entries),
        "coverage_total": total,
        "coverage_exact": total == file_bytes,
        "member_count": len(entries),
        "members": entries,
    }


CAP_BYTES = 80 * 1024


def _graph(members: list[dict]) -> tuple[collections.Counter, dict]:
    """Undirected weight between members that name each other, plus edges by index."""
    index = {m["name"]: i for i, m in enumerate(members)}
    weight: collections.Counter = collections.Counter()
    for i, member in enumerate(members):
        for other in member["references"]:
            j = index.get(other)
            if j is not None and j != i:
                weight[(min(i, j), max(i, j))] += 1
    edges = collections.defaultdict(list)
    for (i, j), count in weight.items():
        edges[j].append((i, count))
    return weight, edges


def _internal_table(members: list[dict], edges: dict) -> dict:
    """internal[(a, b)] = weight of pairs entirely inside members[a:b]."""
    n = len(members)
    internal = {}
    for a in range(n):
        running = 0
        for b in range(a + 1, n + 1):
            for i, count in edges[b - 1]:
                if i >= a:
                    running += count
            internal[(a, b)] = running
    return internal


def _prefix(members: list[dict]) -> list[int]:
    out = [0]
    for member in members:
        out.append(out[-1] + member["bytes"])
    return out


def _score(internal: dict, groups: list[tuple[int, int]]) -> int:
    return sum(internal[g] for g in groups)


def _best_partition(
    members: list[dict], internal: dict, groups: int, low: int, high: int
) -> tuple[list[tuple[int, int]], int]:
    """Contiguous partition into `groups` parts of size in [low, high], best score."""
    n = len(members)
    pre = _prefix(members)
    neg = -(10**9)
    best = [[neg] * (groups + 1) for _ in range(n + 1)]
    cut = [[None] * (groups + 1) for _ in range(n + 1)]
    best[0][0] = 0
    for b in range(1, n + 1):
        for k in range(1, groups + 1):
            for a in range(b - 1, -1, -1):
                size = pre[b] - pre[a]
                if size > high:
                    break
                if size < low:
                    continue
                if best[a][k - 1] == neg:
                    continue
                value = best[a][k - 1] + internal[(a, b)]
                if value > best[b][k]:
                    best[b][k] = value
                    cut[b][k] = a
    result = []
    b, k = n, groups
    while k > 0:
        a = cut[b][k]
        result.append((a, b))
        b, k = a, k - 1
    return list(reversed(result)), best[n][groups]


def _equal_byte_partition(members: list[dict], groups: int) -> list[tuple[int, int]]:
    pre = _prefix(members)
    target = pre[-1] / groups
    out = []
    a = 0
    for k in range(1, groups):
        b = min(range(a + 1, len(members) + 1), key=lambda x: abs(pre[x] - target * k))
        out.append((a, b))
        a = b
    out.append((a, len(members)))
    return out


def cohesion(map_path: str, verification_path: str) -> dict:
    """Score the cut recorded in the split verification report."""
    member_map = json.load(open(map_path, encoding="utf-8"))
    verification = json.load(open(verification_path, encoding="utf-8"))

    chosen_by_source: dict[str, list[tuple[str, int, int]]] = {}
    for entry in verification["files"]:
        first, last = (int(part) for part in entry["source_lines"].split("-"))
        chosen_by_source.setdefault(entry["source"], []).append(
            (entry["file"], first, last)
        )

    report = {
        "issue": 281,
        "command": (
            f"python evidence/281-member-map.py cohesion {map_path} "
            f"{verification_path} > evidence/281-cohesion.json"
        ),
        "metric": (
            "Share of the class's own member-to-member references that stay inside "
            "one file. References come from the 'references' field of the member "
            "map, which is a textual approximation, deliberately over- rather than "
            "under-inclusive. A pair is counted once, undirected."
        ),
        "capBytes": CAP_BYTES,
        "files": [],
    }

    for source in member_map["files"]:
        members = source["members"]
        chosen_ranges = sorted(chosen_by_source[source["file"]], key=lambda r: r[1])
        starts = {m["line_start"]: i for i, m in enumerate(members)}
        ends = {m["line_end"]: i for i, m in enumerate(members)}
        chosen = [(starts[a], ends[b] + 1) for _, a, b in chosen_ranges]
        weight, edges = _graph(members)
        internal = _internal_table(members, edges)
        pairs = sum(weight.values())
        parts = len(chosen)
        sizes = [_prefix(members)[b] - _prefix(members)[a] for a, b in chosen]

        chosen_score = _score(internal, chosen)
        arithmetic = _equal_byte_partition(members, parts)
        arithmetic_score = _score(internal, arithmetic)
        _, cap_score = _best_partition(members, internal, parts, 0, CAP_BYTES)
        band_groups, band_score = _best_partition(
            members, internal, parts, min(sizes), max(sizes)
        )

        def pct(value: int) -> float:
            return round(value / pairs * 100, 1)

        report["files"].append(
            {
                "source": source["file"],
                "parts": parts,
                "memberPairs": pairs,
                "chosen": {
                    "internalPairs": chosen_score,
                    "internalPercent": pct(chosen_score),
                    "partBytes": sizes,
                    "files": [name for name, _, _ in chosen_ranges],
                },
                "arithmeticEqualBytes": {
                    "internalPairs": arithmetic_score,
                    "internalPercent": pct(arithmetic_score),
                    "note": "Same number of parts, cut as close to equal bytes as member boundaries allow.",
                },
                "bestUnderTaskConstraint": {
                    "internalPairs": cap_score,
                    "internalPercent": pct(cap_score),
                    "note": (
                        "Same number of parts, only the task's own 80 KB ceiling. This is "
                        "the honest ceiling of the metric, and it is degenerate: it is "
                        "reached by a few parts at the ceiling next to parts of one member, "
                        "which is not a cut anyone would ship. It is reported because a "
                        "ceiling computed under a constraint borrowed from the chosen cut "
                        "would flatter the choice."
                    ),
                },
                "bestWithinChosenSizeBand": {
                    "internalPairs": band_score,
                    "internalPercent": pct(band_score),
                    "band": [min(sizes), max(sizes)],
                    "firstLines": [members[a]["line_start"] for a, _ in band_groups],
                    "note": (
                        "Same number of parts, each between the smallest and the largest "
                        "part of the chosen cut. The band comes from the chosen cut itself, "
                        "so this number describes the neighbourhood of that cut and cannot "
                        "be used to justify it."
                    ),
                },
            }
        )
    return report


def main() -> None:
    args = sys.argv[1:]
    if args and args[0] == "cohesion":
        if len(args) != 3:
            raise SystemExit(__doc__)
        json.dump(cohesion(args[1], args[2]), sys.stdout, indent=2, ensure_ascii=False)
        sys.stdout.write("\n")
        return
    if not args or len(args) % 2:
        raise SystemExit(__doc__)
    result = {
        "issue": 281,
        "command": (
            "python evidence/281-member-map.py "
            + " ".join(args)
            + " > evidence/281-member-map.json"
        ),
        "note": (
            "Byte coverage is exact by construction: header + sum(member spans, "
            "each including the trivia up to the next member) + footer == file_bytes. "
            "'references' lists sibling member names that appear as whole words in "
            "the member's own text (declaration plus body); it is a textual "
            "approximation of the intra-file call graph, deliberately over- rather "
            "than under-inclusive."
        ),
        "files": [],
    }
    for path, cls in zip(args[0::2], args[1::2]):
        result["files"].append(analyse(path, cls))
    json.dump(result, sys.stdout, indent=2, ensure_ascii=False)
    sys.stdout.write("\n")
    for f in result["files"]:
        if not f["coverage_exact"]:
            raise SystemExit(
                f"COVERAGE MISMATCH for {f['file']}: "
                f"{f['coverage_total']} != {f['file_bytes']}"
            )


if __name__ == "__main__":
    main()
