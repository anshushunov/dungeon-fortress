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
"""

from __future__ import annotations

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


def main() -> None:
    args = sys.argv[1:]
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
