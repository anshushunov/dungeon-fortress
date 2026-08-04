"""Composite the goblin cutout rig at a pose and measure the seams it opens.

Issue #244 inherits a named risk from ADR 0020 and from the review of Issue
#243: "стыки частей видны, поворот руки вокруг сустава на мелком масштабе легко
читается как поломка". `evidence/243-goblin-cutout-joint-rotation-check.png`
shows it at `arm_near` -15 deg and -10 deg. The strike chain needs larger angles
than the +-15 deg the builder checked, so the angles this repository ships have
to be chosen against a measurement rather than against a look.

What is measured
----------------
A *hole* is a transparent pixel of the posed composite that is enclosed by
opaque pixels of that same composite. It is the objective form of "щель":
material moved away and left the floor showing through the middle of a body.
Pixels the silhouette simply no longer covers -- the arm swinging out of its
rest place -- are connected to the outside and are not holes; they are the
animation doing its job.

A second number, *revealed*, counts pixels that were opaque in the rest pose and
are transparent now. It is reported because a large revealed area with zero
holes is still worth looking at: it means a limb left a bay open at the
silhouette edge.

Both are counted in source-cell pixels (the 512x512 space the rig is authored
in) and converted to the runtime scale the game draws at.

Usage
-----
    python evidence/244-measure-rig-gaps.py --sweep
    python evidence/244-measure-rig-gaps.py --chain --sheet .artifacts/244/chain.png
    python evidence/244-measure-rig-gaps.py --json evidence/244-rig-gaps.json
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


def identity() -> tuple[float, ...]:
    return (1.0, 0.0, 0.0, 0.0, 1.0, 0.0)


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
    part_layers: dict[str, Image.Image] | None = None,
) -> Image.Image:
    """One posed 512x512 RGBA cell, drawn back to front by the rig's z_index."""
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
        if part_layers is not None:
            part_layers[part["name"]] = layer
        canvas = Image.alpha_composite(canvas, layer)
    return canvas


def joint_revealed_pixels(
    rest: Image.Image,
    posed: Image.Image,
    joint: tuple[int, int],
    radius: int,
) -> int:
    """Material that stood at a joint in the rest pose and is background now.

    This is the number the angles of this Issue are chosen by, and it is the
    only one of the three that goes to zero on the rest pose by construction.
    `hole_pixels` misses the defect the review of Issue #243 photographed at
    `arm_near` -15 deg, because that wedge is open to the outside rather than
    enclosed; `joint_gap_pixels` misses it too, because it opens further down
    the seam than the builder's 14 px pivot collar reaches. What the eye reads
    as a broken shoulder is simply this: the limb rotated out of the shoulder
    and its parent did not have material behind to take over, so the floor shows
    through where the two are supposed to meet.

    Counted inside a disc around the joint rather than over the whole body,
    because a limb is *supposed* to vacate its rest place further out -- that is
    the animation happening, and counting it would rank a big swing as a worse
    seam than a small one.
    """
    width, height = rest.size
    before = alpha_mask(rest)
    after = alpha_mask(posed)
    cx, cy = joint
    count = 0
    for y in range(max(0, cy - radius), min(height, cy + radius + 1)):
        for x in range(max(0, cx - radius), min(width, cx + radius + 1)):
            if (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius:
                if before[y][x] and not after[y][x]:
                    count += 1
    return count


def alpha_mask(image: Image.Image) -> list[list[bool]]:
    width, height = image.size
    data = image.getdata(3)
    return [
        [data[y * width + x] >= ALPHA_OPAQUE for x in range(width)]
        for y in range(height)
    ]


def hole_pixels(image: Image.Image) -> int:
    """Transparent pixels that opaque material completely encloses."""
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


def joint_positions(rig: dict) -> dict[str, tuple[int, int]]:
    return {
        part["name"]: (
            part["rest_position"][0] + part["pivot"][0],
            part["rest_position"][1] + part["pivot"][1],
        )
        for part in rig["parts"]
    }


def joint_gap_pixels(image: Image.Image, joint: tuple[int, int], radius: int) -> int:
    """Transparent pixels inside the collar the builder duplicated around a pivot.

    The builder states the guarantee this measures: "fully opaque source pixels
    are duplicated in a 14 px radius around each pivot so small rotations have
    real overlap instead of seams". A disc centred on a pivot is invariant under
    rotation about that pivot, so as long as the disc stays opaque the two parts
    are still joined and the seam cannot be seen through. A transparent pixel
    inside it is exactly the wedge the review of #243 photographed -- and, unlike
    a hole, it is still counted when the wedge is open to the outside, which is
    what `hole_pixels` misses.
    """
    width, height = image.size
    opaque = alpha_mask(image)
    cx, cy = joint
    count = 0
    for y in range(max(0, cy - radius), min(height, cy + radius + 1)):
        for x in range(max(0, cx - radius), min(width, cx + radius + 1)):
            if (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius and not opaque[y][x]:
                count += 1
    return count


def revealed_pixels(rest: Image.Image, posed: Image.Image) -> int:
    rest_mask = alpha_mask(rest)
    posed_mask = alpha_mask(posed)
    height = len(rest_mask)
    width = len(rest_mask[0])
    return sum(
        1
        for y in range(height)
        for x in range(width)
        if rest_mask[y][x] and not posed_mask[y][x]
    )


def runtime_scale(rig: dict) -> float:
    """Source-cell pixels to runtime canvas pixels, from the rig's own metadata."""
    x0, y0, x1, y1 = rig["source_body_bbox"]
    target_w, target_h = rig["runtime_target_size"]
    return ((target_w / (x1 - x0)) + (target_h / (y1 - y0))) / 2.0


# The chain the runtime plays, as source-space degrees per part. Kept here so
# the measurement and StrikeChain.cs can be compared by a human without either
# one being the other's copy: StrikeChain.cs is the shipped source of truth and
# these keys are named exactly as its phases are.
CHAIN = {
    "Stance": {},
    "Windup": {
        "arm_near": 34.0,
        "arm_far": -8.0,
        "torso": 5.0,
        "head": 3.0,
        "leg_near": -4.0,
        "leg_far": 5.0,
    },
    "Strike": {
        "arm_near": -12.0,
        "arm_far": 10.0,
        "torso": -7.0,
        "head": -5.0,
        "leg_near": 6.0,
        "leg_far": -6.0,
    },
    "FollowThrough": {
        "arm_near": -4.0,
        "arm_far": 6.0,
        "torso": -4.0,
        "head": -3.0,
        "leg_near": 4.0,
        "leg_far": -4.0,
    },
    "Recover": {
        "arm_near": 8.0,
        "arm_far": -2.0,
        "torso": 2.0,
        "head": 1.0,
        "leg_near": -1.0,
        "leg_far": 1.0,
    },
}

FLINCH = {
    "Stance": {},
    "Impact": {
        "torso": -13.0,
        "head": -9.0,
        "arm_near": 16.0,
        "arm_far": -10.0,
        "leg_near": -7.0,
        "leg_far": 8.0,
    },
    "Settle": {
        "torso": -5.0,
        "head": -3.0,
        "arm_near": 6.0,
        "arm_far": -4.0,
        "leg_near": -3.0,
        "leg_far": 3.0,
    },
}


COLLAR_RADIUS = 14

# 40 source pixels is 20.4 runtime canvas pixels and 6.6 world pixels at the
# shipped 40 px tile -- about a tenth of a drawn body. A seam smaller than the
# disc it is measured in cannot be hidden by the neighbouring part, so the disc
# has to be wider than the pivot collar and narrower than the limb.
JOINT_RADIUS = 40


def measure(rig: dict, rig_dir: str, angles: dict[str, float], rest: Image.Image):
    posed = compose(rig, rig_dir, angles=angles)
    joints = joint_positions(rig)
    collar = {
        name: joint_gap_pixels(posed, joint, COLLAR_RADIUS)
        for name, joint in joints.items()
        if name != "weapon"
    }
    seams = {
        name: joint_revealed_pixels(rest, posed, joint, JOINT_RADIUS)
        for name, joint in joints.items()
        if name != "weapon"
    }
    return posed, hole_pixels(posed), revealed_pixels(rest, posed), collar, seams


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
    scale = runtime_scale(rig)
    report = {
        "rig": os.path.join(arguments.rig_dir, RIG_FILE).replace("\\", "/"),
        "sourceToRuntimeScale": round(scale, 6),
        "restHolePixels": hole_pixels(rest),
        "sweep": [],
        "chain": [],
    }

    if arguments.sweep:
        for part in ("arm_near", "arm_far", "torso", "head", "leg_near", "leg_far"):
            for degrees in (-40, -30, -20, -15, -10, -5, 5, 10, 15, 20, 30, 40):
                _, holes, revealed, collar, wide = measure(
                    rig, arguments.rig_dir, {part: degrees}, rest
                )
                report["sweep"].append(
                    {
                        "part": part,
                        "degrees": degrees,
                        "holePixels": holes,
                        "revealedPixels": revealed,
                        "collarGapPixels": collar[part],
                        "wideGapPixels": wide[part],
                    }
                )
                print(
                    f"{part:9s} {degrees:+4d} deg  collar={collar[part]:4d}  "
                    f"wide={wide[part]:4d}  holes={holes:5d}  revealed={revealed:5d}"
                )

    if arguments.chain or arguments.sheet:
        panels = []
        for table, prefix in ((CHAIN, "strike"), (FLINCH, "flinch")):
            for name, angles in table.items():
                posed, holes, revealed, collar, wide = measure(
                    rig, arguments.rig_dir, angles, rest
                )
                report["chain"].append(
                    {
                        "chain": prefix,
                        "phase": name,
                        "angles": angles,
                        "holePixels": holes,
                        "revealedPixels": revealed,
                        "collarGapPixels": collar,
                        "worstCollarGapPixels": max(collar.values()),
                        "worstWideGapPixels": max(wide.values()),
                    }
                )
                panels.append((f"{prefix}.{name}", posed))
                print(
                    f"{prefix}.{name:14s} worstCollar={max(collar.values()):4d}  "
                    f"worstWide={max(wide.values()):4d}  holes={holes:5d}  "
                    f"revealed={revealed:5d}"
                )
        if arguments.sheet:
            columns = 4
            rows = (len(panels) + columns - 1) // columns
            sheet = Image.new(
                "RGBA", (columns * 512, rows * 512), (51, 47, 66, 255)
            )
            for index, (_, panel) in enumerate(panels):
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
