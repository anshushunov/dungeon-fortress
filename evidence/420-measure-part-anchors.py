#!/usr/bin/env python3
"""Issue #420 — where a mark on a body part has to land.

Reads the shipped cutout parts (ADR 0020's rig, assets/generated/goblins/
cutout_v1) and answers one question per part: where are that part's own
pixels, in the coordinates the rig itself is stated in — source-cell pixels
of the 512x512 idle cell the parts were cut from.

Three numbers per part, all measured off the alpha channel and none of them
typed in by hand:

* ``bbox``      — the part's opaque bounding box in source-cell pixels.
* ``centroid``  — its alpha-weighted centre of mass. This is the anchor a
                  mark wants: the middle of the pixels a player sees, not the
                  middle of a rectangle that may be mostly empty.
* ``joint``     — ``rest_position + pivot`` from the rig file, printed for
                  comparison only. A joint is a shoulder or a hip, i.e. the
                  edge of the part, which is exactly why it is the wrong
                  anchor for a mark.

The same numbers are then carried into the two spaces the runtime draws in,
so that ``InjuryMarks`` can be checked against this file rather than against
an opinion:

* canvas pixels of the 272x192 sprite canvas (``BodyRig.CanvasPointOf``);
* reference pixels relative to a body's render centre, which is the space
  ``Main.ScaleWorld`` multiplies (``CameraView``: a body's canvas is
  ``20 * 1.70`` reference px tall and its top sits ``-24.125`` reference px
  above the render centre).

Run:

    python evidence/420-measure-part-anchors.py > evidence/420-part-anchors.json

Requires Pillow. Reads only; writes nothing but its own stdout.
"""

from __future__ import annotations

import json
import pathlib
import sys

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
RIG_DIR = ROOT / "src/DungeonFortress.Game/assets/generated/goblins/cutout_v1"
RIG_FILE = RIG_DIR / "goblin_cutout_rig_v1.json"

# CameraView.cs, and nothing here re-decides them.
SPRITE_CANVAS_WIDTH = 272.0
SPRITE_CANVAS_HEIGHT = 192.0
SPRITE_OPAQUE_TOP = 20.0          # CameraView.SpriteOpaqueTop == BodyRig.CanvasTop
REFERENCE_GOBLIN_DRAW_SIZE = 20.0  # CameraView.ReferenceGoblinDrawSize
BODY_VISUAL_SCALE = 1.70           # CameraView.BodyVisualScale
GROUND_LINE_SUPPORT_FRACTION = 92.0 / 96.0
SPRITE_SUPPORT_FRACTION = 188.0 / 192.0


def part_alpha_geometry(path: pathlib.Path, rest: tuple[float, float]):
    """Opaque bbox and alpha-weighted centroid of one part, in source-cell px."""
    with Image.open(path) as image:
        alpha = image.convert("RGBA").split()[3]
    width, height = alpha.size
    data = alpha.load()

    total = 0.0
    sum_x = 0.0
    sum_y = 0.0
    min_x, min_y = width, height
    max_x, max_y = -1, -1
    for y in range(height):
        for x in range(width):
            a = data[x, y]
            if a == 0:
                continue
            total += a
            sum_x += a * (x + 0.5)
            sum_y += a * (y + 0.5)
            min_x = min(min_x, x)
            min_y = min(min_y, y)
            max_x = max(max_x, x)
            max_y = max(max_y, y)

    if total == 0.0:
        raise SystemExit(f"{path.name} has no opaque pixel at all.")

    return {
        "pngSize": [width, height],
        "bbox": [
            rest[0] + min_x,
            rest[1] + min_y,
            rest[0] + max_x + 1,
            rest[1] + max_y + 1,
        ],
        "centroid": [rest[0] + sum_x / total, rest[1] + sum_y / total],
    }


def main() -> int:
    rig = json.loads(RIG_FILE.read_text(encoding="utf-8"))
    box = rig["source_body_bbox"]
    target = rig["runtime_target_size"]
    source_to_canvas = target[1] / (box[3] - box[1])
    canvas_left = (SPRITE_CANVAS_WIDTH - ((box[2] - box[0]) * source_to_canvas)) / 2.0

    # Canvas px -> reference px, one factor for both axes because the drawn
    # canvas keeps the pack's 17:12 shape (CameraView.GoblinDrawRect).
    canvas_to_reference = (REFERENCE_GOBLIN_DRAW_SIZE * BODY_VISUAL_SCALE) / SPRITE_CANVAS_HEIGHT
    canvas_top_reference = (
        REFERENCE_GOBLIN_DRAW_SIZE * (GROUND_LINE_SUPPORT_FRACTION - 0.5)
        - REFERENCE_GOBLIN_DRAW_SIZE * BODY_VISUAL_SCALE * SPRITE_SUPPORT_FRACTION
    )

    def to_canvas(point):
        return [
            canvas_left + ((point[0] - box[0]) * source_to_canvas),
            SPRITE_OPAQUE_TOP + ((point[1] - box[1]) * source_to_canvas),
        ]

    def to_reference(point):
        canvas = to_canvas(point)
        return [
            (canvas[0] - (SPRITE_CANVAS_WIDTH / 2.0)) * canvas_to_reference,
            canvas_top_reference + (canvas[1] * canvas_to_reference),
        ]

    parts = {}
    for part in rig["parts"]:
        rest = part["rest_position"]
        pivot = part["pivot"]
        geometry = part_alpha_geometry(RIG_DIR / part["file"], rest)
        joint = [rest[0] + pivot[0], rest[1] + pivot[1]]
        parts[part["name"]] = {
            "sourceCell": {
                "restPosition": rest,
                "joint": joint,
                **geometry,
            },
            "canvas": {
                "centroid": to_canvas(geometry["centroid"]),
                "joint": to_canvas(joint),
            },
            "reference": {
                "centroid": to_reference(geometry["centroid"]),
                "joint": to_reference(joint),
            },
        }

    report = {
        "schemaVersion": 1,
        "issue": 420,
        "what": (
            "Где на силуэте лежат пиксели каждой части рига: bbox и "
            "альфа-взвешенный центр масс, снятые с поставляемых PNG, "
            "пересчитанные в канву 272x192 и в опорные пиксели относительно "
            "точки отрисовки тела."
        ),
        "command": "python evidence/420-measure-part-anchors.py",
        "rig": {
            "file": str(RIG_FILE.relative_to(ROOT)).replace("\\", "/"),
            "sourceBodyBbox": box,
            "runtimeTargetSize": target,
            "sourceToCanvas": source_to_canvas,
            "canvasLeft": canvas_left,
            "canvasToReference": canvas_to_reference,
            "canvasTopReference": canvas_top_reference,
        },
        "parts": parts,
    }
    json.dump(report, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
