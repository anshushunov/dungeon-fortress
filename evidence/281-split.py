#!/usr/bin/env python3
"""Issue #281: cut Main.cs and PrototypeWorld.cs into partial-class files.

The cut moves whole *contiguous runs of members* and nothing else. Every output
file is therefore a generated header plus a verbatim line range of the base
file plus a closing brace, which is what `verify` proves.

    python evidence/281-split.py split  --base HEAD
    python evidence/281-split.py verify --base <rev-before-the-cut> \
        > evidence/281-split-verification.json

`split` reads the source files from the working tree (they must still be whole);
`verify` reads the base file from git so that the proof does not depend on the
working tree at all.
"""

from __future__ import annotations

import difflib
import json
import subprocess
import sys

SIM = "src/DungeonFortress.Simulation/PrototypeWorld.cs"
GAME = "src/DungeonFortress.Game/Main.cs"

# (output path, first source line, last source line, one-line responsibility)
PLAN = {
    SIM: {
        "namespace": "DungeonFortress.Simulation",
        "declaration": "public sealed partial class PrototypeWorld",
        "header_lines": 6,          # lines 1..6 of the source stay in file 1
        "header_patch": (
            5,
            "public sealed class PrototypeWorld",
            "public sealed partial class PrototypeWorld",
        ),
        "parts": [
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.cs", 7, 623,
                "The state the world holds, how it is built, how it advances one\n"
                "tick, and the canonical document it publishes.",
            ),
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.Commands.cs", 625, 1001,
                "Player commands: applying one, and putting right what applying it\n"
                "invalidated — zones, designations and the jobs that stood on them.",
            ),
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.Planning.cs", 1003, 1745,
                "What the tick decides before anybody moves: which jobs exist, what\n"
                "each creature needs, and how bodies get out of each other's way.",
            ),
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.Matching.cs", 1747, 2342,
                "Which creature gets which job, and the record of why — the chosen\n"
                "pair, the counterfactual probe and the waiting reasons.",
            ),
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.Acting.cs", 2344, 2607,
                "The act a creature performs on its tick: the dispatcher, and what a\n"
                "creature does when it is off duty.",
            ),
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.Combat.cs", 2609, 3381,
                "The raid: waves announced and arriving, raiders acting, the fight\n"
                "resolved, and what it leaves behind — morale, renown, memory of place.",
            ),
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.Work.cs", 3383, 4156,
                "Carrying the work out: mustering, eating, working a job, stone in\n"
                "hand, finishing or cancelling a job, and moving a body one step.",
            ),
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.Stores.cs", 4158, 5002,
                "What the domain has put away and who has claimed it: meals, beds,\n"
                "stockpile and build-site stone, their reservations and revalidation.",
            ),
            (
                "src/DungeonFortress.Simulation/PrototypeWorld.State.cs", 5004, 5534,
                "The live state types the world mutates, and their projection into the\n"
                "snapshot records the outside world reads.",
            ),
        ],
    },
    GAME: {
        "namespace": "DungeonFortress.Game",
        "declaration": "public partial class Main",
        "header_lines": 16,         # lines 1..16 of the source stay in file 1
        "header_patch": None,       # Main is already declared partial
        # `using System.Globalization;` is used only by FormatNumber, which the
        # cut moves to Main.Verification.cs, so it leaves Main.cs with it.
        "header_drop": [1],
        "parts": [
            (
                "src/DungeonFortress.Game/Main.cs", 17, 937,
                "The node itself: the state the adapter holds, the Godot callbacks it\n"
                "answers, and the frame-pacing probe those callbacks feed.",
            ),
            (
                "src/DungeonFortress.Game/Main.Frame.cs", 939, 1551,
                "The frame the game is shown in: window size, UI scale, the camera and\n"
                "the mapping between a point on screen and a cell in the world.",
            ),
            (
                "src/DungeonFortress.Game/Main.Hud.cs", 1553, 2213,
                "The HUD's widgets: map column, control strips, buttons and their\n"
                "icons, the side column and the legend.",
            ),
            (
                "src/DungeonFortress.Game/Main.Session.cs", 2215, 2620,
                "What a run loads and what it refreshes: sprites and the cutout rig,\n"
                "the fixture, one advanced tick, and the labels that follow it.",
            ),
            (
                "src/DungeonFortress.Game/Main.HudText.cs", 2622, 3263,
                "The words the HUD shows, the layout pass that places them, and the\n"
                "guards that say they fit the frame and stay readable.",
            ),
            (
                "src/DungeonFortress.Game/Main.DrawWorld.cs", 3265, 3911,
                "The view state a frame is drawn from, and drawing the world it\n"
                "describes: floors, rooms, zones, walls, routes and loose items.",
            ),
            (
                "src/DungeonFortress.Game/Main.DrawBodies.cs", 3913, 4517,
                "Drawing the bodies: the cutout rig, the side silhouette, the blow\n"
                "flash, sparks and streaks, damage numbers and the information over a body.",
            ),
            (
                "src/DungeonFortress.Game/Main.DrawOverlays.cs", 4519, 5075,
                "What is drawn over the cells rather than in them: brush preview and\n"
                "selection, dig designations, build sites and blueprints, stockpiles.",
            ),
            (
                "src/DungeonFortress.Game/Main.Player.cs", 5077, 5700,
                "What the player's input does to the world — brushes, selection, pause,\n"
                "speed, one applied command — and the scripted demo runs that stand in for it.",
            ),
            (
                "src/DungeonFortress.Game/Main.Verification.cs", 5702, 6329,
                "The headless runs an agent asks for: the brush and marking smokes, the\n"
                "screenshot, the deterministic fixture and the result the CLI prints.",
            ),
            (
                "src/DungeonFortress.Game/Main.Rendering.cs", 6331, 6876,
                "Rendering primitives the drawing files call: colours and legend words,\n"
                "which sprite a body uses, which way it faces, how a blow moves it, and\n"
                "the small draws that sit at the end of the source.",
            ),
        ],
    },
}


def read_lines(path: str) -> list[str]:
    with open(path, "rb") as handle:
        return handle.read().decode("utf-8").split("\n")


def git_lines(rev: str, path: str) -> list[str]:
    blob = subprocess.run(
        ["git", "show", f"{rev}:{path}"],
        check=True,
        stdout=subprocess.PIPE,
    ).stdout
    return blob.decode("utf-8").split("\n")


def generated_header(spec: dict, note: str) -> list[str]:
    out = list(spec["usings"])
    if out:
        out.append("")
    out.append(f"namespace {spec['namespace']};")
    out.append("")
    for line in note.split("\n"):
        out.append(f"// {line}")
    out.append(spec["declaration"])
    out.append("{")
    return out


# Usings kept per generated file. Decided by a build with IDE0005 ("using
# directive is unnecessary") promoted to a warning through a throwaway
# .editorconfig: every generated file started with the full using list of its
# source and kept only what that build did not name.
#
# PrototypeWorld.cs's own `using System.Text.Json;` is unnecessary and was
# already unnecessary before this change. It is left exactly where it was: the
# cut does not fix what it finds.
GAME_USINGS = [
    "using DungeonFortress.Presentation;",
    "using DungeonFortress.Simulation;",
    "",
    "using Godot;",
]

USINGS = {
    "src/DungeonFortress.Simulation/PrototypeWorld.Commands.cs": [],
    "src/DungeonFortress.Simulation/PrototypeWorld.Planning.cs": [],
    "src/DungeonFortress.Simulation/PrototypeWorld.Matching.cs": [],
    "src/DungeonFortress.Simulation/PrototypeWorld.Acting.cs": [],
    "src/DungeonFortress.Simulation/PrototypeWorld.Combat.cs": [],
    "src/DungeonFortress.Simulation/PrototypeWorld.Work.cs": [],
    "src/DungeonFortress.Simulation/PrototypeWorld.Stores.cs": [],
    "src/DungeonFortress.Simulation/PrototypeWorld.State.cs": [],
    "src/DungeonFortress.Game/Main.Frame.cs": GAME_USINGS,
    "src/DungeonFortress.Game/Main.Hud.cs": GAME_USINGS,
    "src/DungeonFortress.Game/Main.Session.cs": GAME_USINGS,
    "src/DungeonFortress.Game/Main.HudText.cs": GAME_USINGS,
    "src/DungeonFortress.Game/Main.DrawWorld.cs": GAME_USINGS,
    "src/DungeonFortress.Game/Main.DrawBodies.cs": GAME_USINGS,
    "src/DungeonFortress.Game/Main.DrawOverlays.cs": GAME_USINGS,
    "src/DungeonFortress.Game/Main.Player.cs": GAME_USINGS,
    # The only generated file that formats a number and serialises JSON.
    "src/DungeonFortress.Game/Main.Verification.cs": [
        "using System.Globalization;",
        "using System.Text.Json;",
        "",
    ] + GAME_USINGS,
    "src/DungeonFortress.Game/Main.Rendering.cs": GAME_USINGS,
}


def split(base: str) -> None:
    for source, spec in PLAN.items():
        lines = git_lines(base, source)
        head = lines[: spec["header_lines"]]
        patch = spec["header_patch"]
        if patch:
            index, old, new = patch
            assert head[index - 1] == old, (head[index - 1], old)
            head[index - 1] = new
        for index in sorted(spec.get("header_drop", []), reverse=True):
            assert head[index - 1].startswith("using "), head[index - 1]
            del head[index - 1]
        for path, first, last, note in spec["parts"]:
            body = lines[first - 1:last]
            if path == source:
                out = head + body + ["}", ""]
            else:
                header = generated_header(
                    {
                        "usings": USINGS[path],
                        "namespace": spec["namespace"],
                        "declaration": spec["declaration"],
                    },
                    note,
                )
                out = header + body + ["}", ""]
            with open(path, "wb") as handle:
                handle.write("\n".join(out).encode("utf-8"))
            print(f"wrote {path}: source lines {first}-{last}, {len(out)} lines")


def verify(base: str) -> None:
    report = {"issue": 281, "base": base, "files": []}
    ok = True
    for source, spec in PLAN.items():
        base_lines = git_lines(base, source)
        for path, first, last, note in spec["parts"]:
            expected = base_lines[first - 1:last]
            produced = read_lines(path)
            # locate the body: it starts after the generated/kept header and
            # ends before the file's own final "}" and trailing empty line.
            header_len = len(produced) - len(expected) - 2
            actual = produced[header_len:header_len + len(expected)]
            tail = produced[header_len + len(expected):]
            diff = [
                line
                for line in difflib.unified_diff(expected, actual, n=0, lineterm="")
                if line[:1] in "+-" and line[:3] not in ("+++", "---")
            ]
            entry = {
                "file": path,
                "source": source,
                "source_lines": f"{first}-{last}",
                "body_line_count": len(expected),
                "body_differences": len(diff),
                "header_line_count": header_len,
                "tail": tail,
                "verbatim": not diff and tail == ["}", ""],
            }
            if diff:
                entry["diff_sample"] = diff[:20]
            ok = ok and entry["verbatim"]
            report["files"].append(entry)
    report["all_verbatim"] = ok
    json.dump(report, sys.stdout, indent=2, ensure_ascii=False)
    sys.stdout.write("\n")
    if not ok:
        raise SystemExit("NOT VERBATIM")


def main() -> None:
    if len(sys.argv) < 4 or sys.argv[2] != "--base":
        raise SystemExit(__doc__)
    mode, base = sys.argv[1], sys.argv[3]
    if mode == "split":
        split(base)
    elif mode == "verify":
        verify(base)
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()
