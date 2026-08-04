"""Composite the goblin cutout rig at a pose and measure the seams it opens.

Issue #244 inherits a named risk from ADR 0020 and from the review of Issue
#243: "стыки частей видны, поворот руки вокруг сустава на мелком масштабе легко
читается как поломка". `evidence/243-goblin-cutout-joint-rotation-check.png`
shows it at `arm_near` -15 deg and -10 deg, and the review of this Issue's brief
found a second one on the far arm. The strike chain needs larger angles than the
+-15 deg the builder checked, so the angles this repository ships are chosen
against a measurement rather than against a look.

What is measured
----------------
`slit_pixels` is the number this Issue gates on. A pixel counts when the rest
pose had material there, the posed one does not, and the posed body still has
material on both sides of it along its own row -- i.e. the floor is visible
*through* the body. That is exactly the defect the review photographed: two
parts that no longer meet. It is zero on the rest pose by construction.

`hole_pixels` -- transparent pixels wholly enclosed by opaque ones -- is
reported and deliberately not gated on. It misses the photographed defect
entirely, because that wedge is open to the outside; keeping it in the report is
what makes that statement checkable rather than asserted.

`revealed_pixels` -- what the rest pose covered and this one does not -- is
reported for the same reason. A limb that moves is supposed to leave the
silhouette it had; that is the animation happening.

All three are counted in source-cell pixels, the 512x512 space the rig is
authored in.

Usage
-----
    python evidence/244-measure-rig-gaps.py --sweep
    python evidence/244-measure-rig-gaps.py --chain --sheet .artifacts/244/chain.png
    python evidence/244-measure-rig-gaps.py --sweep --chain --json evidence/244-rig-gaps.json
"""

from __future__ import annotations

import argparse
import json
import math
import os
from collections import deque

from PIL import Image

RIG_DIR = os.path.join(
    "src", "DungeonFortress.Game", "assets", "generated", "goblins", "cutout_v1"
)
RIG_FILE = "goblin_cutout_rig_v1.json"
ALPHA_OPAQUE = 32


def load_rig(rig_dir: str) -> dict:
    with open(os.path.join(rig_dir, RIG_FILE), "r", encoding="utf-8") as handle:
        return json.load(handle)


def multiply(a: tuple[float, ...], b: tuple[float, ...]) -> tuple[float, ...]:
    """Compose two affine 2x3 maps: the result applies b first, then a."""
    return (
        a[0] * b[0] + a[1] * b[3],
        a[0] * b[1] + a[1] * b[4],
        a[0] * b[2] + a[1] * b[5] + a[2],
        a[3] * b[0] + a[4] * b[3],
        a[3] * b[1] + a[4] * b[4],
        a[3] * b[2] + a[4] * b[5] + a[5],
    )


def rotate_about(pivot: tuple[float, float], degrees: float) -> tuple[float, ...]:
    radians = math.radians(degrees)
    cos, sin = math.cos(radians), math.sin(radians)
    px, py = pivot
    return (
        cos,
        -sin,
        px - cos * px + sin * py,
        sin,
        cos,
        py - sin * px - cos * py,
    )


def translate(dx: float, dy: float) -> tuple[float, ...]:
    return (1.0, 0.0, dx, 0.0, 1.0, dy)


def invert(m: tuple[float, ...]) -> tuple[float, ...]:
    determinant = m[0] * m[4] - m[1] * m[3]
    if abs(determinant) < 1e-12:
        raise ValueError("singular affine transform")
    a = m[4] / determinant
    b = -m[1] / determinant
    d = -m[3] / determinant
    e = m[0] / determinant
    c = -(a * m[2] + b * m[5])
    f = -(d * m[2] + e * m[5])
    return (a, b, c, d, e, f)


def compose(
    rig: dict,
    rig_dir: str,
    angles: dict[str, float] | None = None,
    offsets: dict[str, tuple[float, float]] | None = None,
    show_weapon: bool = False,
) -> Image.Image:
    """One posed 512x512 RGBA cell, drawn back to front by the rig's z_index.

    The hierarchy is the rig's: a part's own transform turns it about its joint
    and slides it, and a child's transform is its parent's times its own. That
    is the same composition `Main.RigLayout` builds in the engine, written twice
    on purpose -- this side has no Godot in it, so the measurement can be
    reproduced from a shell.
    """
    angles = angles or {}
    offsets = offsets or {}
    size = tuple(rig["source_cell_size"])
    parts = {part["name"]: part for part in rig["parts"]}
    transforms: dict[str, tuple[float, ...]] = {}

    def transform_of(name: str) -> tuple[float, ...]:
        if name in transforms:
            return transforms[name]
        part = parts[name]
        joint = (
            part["rest_position"][0] + part["pivot"][0],
            part["rest_position"][1] + part["pivot"][1],
        )
        own = rotate_about(joint, angles.get(name, 0.0))
        dx, dy = offsets.get(name, (0.0, 0.0))
        own = multiply(translate(dx, dy), own)
        parent = part["parent"]
        total = own if parent is None else multiply(transform_of(parent), own)
        transforms[name] = total
        return total

    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    for part in sorted(rig["parts"], key=lambda item: item["z_index"]):
        if part["name"] == "weapon" and not show_weapon:
            continue
        image = Image.open(os.path.join(rig_dir, part["file"])).convert("RGBA")
        placement = multiply(
            transform_of(part["name"]),
            translate(part["rest_position"][0], part["rest_position"][1]),
        )
        layer = image.transform(
            size, Image.AFFINE, invert(placement), resample=Image.NEAREST
        )
        canvas = Image.alpha_composite(canvas, layer)
    return canvas


def alpha_mask(image: Image.Image) -> list[list[bool]]:
    width, height = image.size
    data = list(image.getchannel("A").getdata())
    return [
        [data[y * width + x] >= ALPHA_OPAQUE for x in range(width)]
        for y in range(height)
    ]


def hole_pixels(image: Image.Image) -> int:
    """Transparent pixels that opaque material completely encloses.

    Reported, not gated on: the wedge the review of Issue #243 photographed is
    open to the outside, so this number never saw it. Keeping it here is what
    makes that a measurement rather than a claim.
    """
    width, height = image.size
    opaque = alpha_mask(image)
    seen = [[False] * width for _ in range(height)]
    queue: deque[tuple[int, int]] = deque()
    for x in range(width):
        for y in (0, height - 1):
            if not opaque[y][x] and not seen[y][x]:
                seen[y][x] = True
                queue.append((x, y))
    for y in range(height):
        for x in (0, width - 1):
            if not opaque[y][x] and not seen[y][x]:
                seen[y][x] = True
                queue.append((x, y))
    while queue:
        x, y = queue.popleft()
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < width and 0 <= ny < height:
                if not opaque[ny][nx] and not seen[ny][nx]:
                    seen[ny][nx] = True
                    queue.append((nx, ny))
    return sum(
        1
        for y in range(height)
        for x in range(width)
        if not opaque[y][x] and not seen[y][x]
    )


def revealed_pixels(rest: Image.Image, posed: Image.Image) -> int:
    """Pixels the rest pose covered and the posed one does not."""
    before = alpha_mask(rest)
    after = alpha_mask(posed)
    height = len(before)
    width = len(before[0])
    return sum(
        1
        for y in range(height)
        for x in range(width)
        if before[y][x] and not after[y][x]
    )


# How far a row is searched either side of a transparent pixel for body
# material. 45 source pixels is 23 canvas pixels, a fifth of the body's own
# 116-pixel width: wide enough to span any gap between two parts, narrow enough
# that the empty space beside a body is not counted as being inside it.
SLIT_REACH = 45


def slit_pixels(rest: Image.Image, posed: Image.Image) -> int:
    """Background visible *through* the body: the measured form of "щель"."""
    before = alpha_mask(rest)
    after = alpha_mask(posed)
    height = len(before)
    width = len(before[0])
    count = 0
    for y in range(height):
        row = after[y]
        left = [-1] * width
        nearest = -1
        for x in range(width):
            if row[x]:
                nearest = x
            left[x] = nearest
        nearest = width + SLIT_REACH + 1
        for x in range(width - 1, -1, -1):
            if row[x]:
                nearest = x
            elif (
                before[y][x]
                and left[x] >= 0
                and x - left[x] <= SLIT_REACH
                and nearest - x <= SLIT_REACH
            ):
                count += 1
    return count


def runtime_scale(rig: dict) -> float:
    """Source-cell pixels to runtime canvas pixels, from the rig's own metadata."""
    x0, y0, x1, y1 = rig["source_body_bbox"]
    return rig["runtime_target_size"][1] / (y1 - y0)


# The chain the runtime plays, transcribed from StrikeChain.cs. Angles are
# degrees about each part's own joint; offsets are source-cell pixels. The C#
# file is the shipped source of truth -- this table exists so the measurement
# can be reproduced without starting the engine, and the two are compared by
# eye when either changes.
CHAIN = {
    "Stance": ({}, {}),
    "Windup": (
        {"head": 3, "arm_near": -30, "arm_far": -6, "leg_near": 6, "leg_far": -5},
        {
            "head": (-6, 1),
            "arm_near": (-8, -8),
            "arm_far": (-8, 8),
            "leg_near": (4, 1),
            "leg_far": (-4, -6),
        },
    ),
    "Strike": (
        {"head": -4, "arm_near": 8, "arm_far": 10, "leg_near": 9, "leg_far": -7},
        {
            "head": (7, 1),
            "arm_near": (8, 8),
            "arm_far": (-8, -8),
            "leg_near": (1, 1),
            "leg_far": (-8, 6),
        },
    ),
    "FollowThrough": (
        {"head": -3, "arm_near": 12, "arm_far": 7, "leg_near": 6, "leg_far": -5},
        {
            "head": (5, 0),
            "arm_near": (8, 8),
            "arm_far": (-8, 1),
            "leg_near": (-8, -8),
            "leg_far": (-5, 0),
        },
    ),
    "Recover": (
        {"head": -1, "arm_near": 3, "arm_far": 2, "leg_near": 2, "leg_far": -1},
        {
            "head": (2, 0),
            "arm_near": (8, -1),
            "arm_far": (-5, 6),
            "leg_near": (1, 0),
            "leg_far": (-2, 0),
        },
    ),
}

FLINCH = {
    "Impact": (
        {"head": -7, "arm_near": -8, "arm_far": 6, "leg_near": -4, "leg_far": 5},
        {
            "head": (8, -1),
            "arm_near": (8, 4),
            "arm_far": (-5, 3),
            "leg_near": (-1, 1),
            "leg_far": (-1, 1),
        },
    ),
    "Settle": (
        {"head": -3, "arm_near": -4, "arm_far": 3, "leg_near": -2, "leg_far": 2},
        {
            "head": (5, 0),
            "arm_near": (8, -3),
            "arm_far": (-2, 8),
            "leg_near": (0, 0),
            "leg_far": (-7, 1),
        },
    ),
}

SWEEP_PARTS = ("arm_near", "arm_far", "torso", "head", "leg_near", "leg_far")
SWEEP_ANGLES = (-30, -20, -15, -10, -5, 5, 10, 15, 20, 30)


def measure(rig, rig_dir, angles, offsets, rest):
    posed = compose(rig, rig_dir, angles=angles, offsets=offsets)
    return (
        posed,
        slit_pixels(rest, posed),
        hole_pixels(posed),
        revealed_pixels(rest, posed),
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rig-dir", default=RIG_DIR)
    parser.add_argument("--sweep", action="store_true")
    parser.add_argument("--chain", action="store_true")
    parser.add_argument("--sheet")
    parser.add_argument("--json")
    arguments = parser.parse_args()

    rig = load_rig(arguments.rig_dir)
    rest = compose(rig, arguments.rig_dir)
    report = {
        "rig": os.path.join(arguments.rig_dir, RIG_FILE).replace("\\", "/"),
        "sourceToRuntimeScale": round(runtime_scale(rig), 6),
        "slitReachSourcePixels": SLIT_REACH,
        "restSlitPixels": slit_pixels(rest, rest),
        "restHolePixels": hole_pixels(rest),
        "sweep": [],
        "chain": [],
    }
    print(f"rest: slit={report['restSlitPixels']} holes={report['restHolePixels']}")

    if arguments.sweep:
        for part in SWEEP_PARTS:
            for degrees in SWEEP_ANGLES:
                _, slit, holes, revealed = measure(
                    rig, arguments.rig_dir, {part: degrees}, {}, rest
                )
                report["sweep"].append(
                    {
                        "part": part,
                        "degrees": degrees,
                        "slitPixels": slit,
                        "holePixels": holes,
                        "revealedPixels": revealed,
                    }
                )
                print(
                    f"{part:9s} {degrees:+4d} deg  slit={slit:5d}  "
                    f"holes={holes:5d}  revealed={revealed:5d}"
                )

    if arguments.chain or arguments.sheet:
        panels = []
        for table, name in ((CHAIN, "strike"), (FLINCH, "flinch")):
            for phase, (angles, offsets) in table.items():
                posed, slit, holes, revealed = measure(
                    rig, arguments.rig_dir, angles, offsets, rest
                )
                bare = slit_pixels(rest, compose(rig, arguments.rig_dir, angles=angles))
                report["chain"].append(
                    {
                        "chain": name,
                        "phase": phase,
                        "angles": angles,
                        "offsets": {key: list(value) for key, value in offsets.items()},
                        "slitPixels": slit,
                        "slitPixelsWithoutOffsets": bare,
                        "holePixels": holes,
                        "revealedPixels": revealed,
                    }
                )
                panels.append(posed)
                print(
                    f"{name}.{phase:14s} slit={slit:5d} (no offsets {bare:5d})  "
                    f"holes={holes:5d}  revealed={revealed:5d}"
                )

        if arguments.sheet:
            columns = 4
            rows = (len(panels) + columns - 1) // columns
            sheet = Image.new("RGBA", (columns * 512, rows * 512), (51, 47, 66, 255))
            for index, panel in enumerate(panels):
                sheet.alpha_composite(
                    panel, ((index % columns) * 512, (index // columns) * 512)
                )
            os.makedirs(os.path.dirname(arguments.sheet) or ".", exist_ok=True)
            sheet.save(arguments.sheet)
            print(f"sheet: {arguments.sheet}")

    if arguments.json:
        with open(arguments.json, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(report, handle, indent=2, ensure_ascii=False)
            handle.write("\n")
        print(f"json: {arguments.json}")


if __name__ == "__main__":
    main()
