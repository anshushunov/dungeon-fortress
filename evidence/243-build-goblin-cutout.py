#!/usr/bin/env python3
"""Build the Issue #243 goblin cutout from the v2 alpha sprite sheet.

The input is the 1536x1024 alpha sheet produced by the repository's
scripts/art/remove_chroma_key.py.  Body pixels come from the idle cell; the
separate spear comes from the combat cell.  All coordinates below are in the
512x512 source-cell coordinate system.
"""

from __future__ import annotations

import argparse
from collections import deque
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont


EXPECTED_ALPHA_SIZE = (1536, 1024)
IDLE_BOX = (0, 0, 512, 512)
COMBAT_BOX = (1024, 0, 1536, 512)
SOURCE_BODY_BBOX = (172, 92, 400, 422)
PAD = 8


PARTS = [
    {
        "name": "leg_far",
        "parent": "torso",
        "z_index": 0,
        "pivot_source": (326, 332),
        "polygon": [(310, 318), (352, 317), (375, 338), (390, 374), (385, 412),
                    (304, 412), (296, 374), (300, 340)],
        "motion": "Independent far-leg step and crouch during windup and recovery.",
    },
    {
        "name": "arm_far",
        "parent": "torso",
        "z_index": 1,
        "pivot_source": (352, 236),
        "polygon": [
            [(342, 211), (370, 215), (386, 241), (397, 276), (393, 306),
             (382, 319), (358, 318), (350, 299), (344, 272), (335, 252)],
            [(360, 302), (389, 302), (401, 318), (394, 338), (371, 342),
             (360, 334)],
        ],
        "motion": "Independent far-arm counterbalance and two-handed weapon support.",
    },
    {
        "name": "torso",
        "parent": None,
        "z_index": 2,
        "pivot_source": (286, 330),
        "polygon": [(199, 190), (251, 184), (304, 205), (349, 196), (374, 243), (363, 307),
                    (344, 358), (306, 373), (272, 365), (239, 375), (215, 329), (207, 269)],
        "motion": "Root body part: leans into the strike and recoils independently of both legs.",
    },
    {
        "name": "leg_near",
        "parent": "torso",
        "z_index": 3,
        "pivot_source": (265, 333),
        "polygon": [(221, 318), (277, 318), (298, 339), (309, 374), (304, 414),
                    (283, 429), (207, 429), (191, 404), (195, 365), (207, 337)],
        "motion": "Independent near-leg step and crouch preserve the readable planted stance.",
    },
    {
        "name": "head",
        "parent": "torso",
        "z_index": 4,
        "pivot_source": (303, 250),
        "polygon": [(165, 111), (226, 139), (238, 91), (329, 88), (365, 142), (405, 119),
                    (390, 188), (370, 196), (367, 232), (342, 255), (307, 272), (270, 258),
                    (239, 241), (219, 216), (185, 195)],
        "motion": "Turns toward the target independently from the torso during anticipation and follow-through.",
    },
    {
        "name": "arm_near",
        "parent": "torso",
        "z_index": 5,
        "pivot_source": (219, 232),
        "polygon": [
            [(197, 211), (220, 214), (225, 238), (220, 263), (212, 282),
             (216, 299), (210, 313), (184, 315), (173, 300), (179, 278),
             (176, 253), (184, 226)],
            [(176, 299), (214, 299), (220, 319), (216, 347), (190, 359),
             (171, 341), (166, 318)],
        ],
        "motion": "Primary strike arm rotates around the shoulder through windup, hit, follow-through, and return.",
    },
]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def committed_sha256(path: Path) -> str:
    """Hash bytes as committed under the repository's LF text policy."""
    data = path.read_bytes()
    if path.suffix.lower() in {".json", ".md", ".py"}:
        data = data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return hashlib.sha256(data).hexdigest()


def polygon_mask(points: list, size: tuple[int, int] = (512, 512)) -> Image.Image:
    mask = Image.new("L", size, 0)
    draw = ImageDraw.Draw(mask)
    polygons = [points] if isinstance(points[0][0], int) else points
    for polygon in polygons:
        draw.polygon(polygon, fill=255)
    return mask


def source_pixels(source: Image.Image, mask: Image.Image) -> Image.Image:
    result = source.copy()
    alpha = ImageChops.multiply(source.getchannel("A"), mask)
    result.putalpha(alpha)
    return result


def assign_visible_pixels_by_nearest_part(
    part_masks: dict[str, Image.Image], source_alpha: Image.Image
) -> dict[str, int]:
    """Flood every unclaimed visible edge pixel from the nearest owned pixel."""
    width, height = source_alpha.size
    names = list(part_masks)
    labels = [-1] * (width * height)
    queue: deque[tuple[int, int]] = deque()
    alpha = source_alpha.load()
    mask_pixels = {name: part_masks[name].load() for name in names}
    for label, name in enumerate(names):
        pixels = mask_pixels[name]
        for y in range(height):
            row = y * width
            for x in range(width):
                if alpha[x, y] > 0 and pixels[x, y] > 0:
                    labels[row + x] = label
                    queue.append((x, y))

    assigned = {name: 0 for name in names}
    while queue:
        x, y = queue.popleft()
        label = labels[y * width + x]
        for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if nx < 0 or ny < 0 or nx >= width or ny >= height:
                continue
            index = ny * width + nx
            if alpha[nx, ny] == 0 or labels[index] != -1:
                continue
            labels[index] = label
            name = names[label]
            mask_pixels[name][nx, ny] = 255
            assigned[name] += 1
            queue.append((nx, ny))

    unassigned = sum(
        1 for y in range(height) for x in range(width)
        if alpha[x, y] > 0 and labels[y * width + x] == -1
    )
    if unassigned:
        raise RuntimeError(f"{unassigned} visible source pixels have no reachable semantic part")
    return assigned


def significant_alpha_components(image: Image.Image, minimum_area: int = 16) -> list[int]:
    """Return 8-connected alpha component sizes, ignoring subpixel specks."""
    alpha = image.getchannel("A")
    width, height = alpha.size
    pixels = alpha.load()
    visited: set[tuple[int, int]] = set()
    sizes: list[int] = []
    for y in range(height):
        for x in range(width):
            if pixels[x, y] == 0 or (x, y) in visited:
                continue
            visited.add((x, y))
            queue = deque([(x, y)])
            size = 0
            while queue:
                cx, cy = queue.popleft()
                size += 1
                for nx in range(max(0, cx - 1), min(width, cx + 2)):
                    for ny in range(max(0, cy - 1), min(height, cy + 2)):
                        if pixels[nx, ny] > 0 and (nx, ny) not in visited:
                            visited.add((nx, ny))
                            queue.append((nx, ny))
            if size >= minimum_area:
                sizes.append(size)
    return sorted(sizes, reverse=True)


def crop_with_padding(image: Image.Image, padding: int = PAD) -> tuple[Image.Image, tuple[int, int]]:
    bbox = image.getchannel("A").getbbox()
    if bbox is None:
        raise ValueError("part has no visible pixels")
    left = max(0, bbox[0] - padding)
    top = max(0, bbox[1] - padding)
    right = min(image.width, bbox[2] + padding)
    bottom = min(image.height, bbox[3] + padding)
    return image.crop((left, top, right, bottom)), (left, top)


def add_hidden_joint_fill(
    part_name: str,
    image: Image.Image,
    higher_opaque: Image.Image,
) -> Image.Image:
    """Paint only pixels that are fully hidden by higher rest-pose layers."""
    patch = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(patch)
    outline = (31, 29, 22, 255)
    teal = (31, 82, 79, 255)
    teal_light = (48, 105, 96, 255)
    skin = (144, 144, 48, 255)
    skin_light = (194, 185, 75, 255)

    if part_name == "leg_far":
        # Hidden upper/lower-leg bridge under torso and the near leg.
        draw.polygon([(310, 320), (341, 318), (365, 384), (350, 408),
                      (321, 401), (316, 354)], fill=outline)
        draw.polygon([(316, 326), (336, 325), (357, 381), (346, 399),
                      (327, 395), (323, 353)], fill=(105, 67, 38, 255))
        draw.line([(323, 337), (343, 333)], fill=(167, 103, 56, 255), width=4)
    elif part_name == "torso":
        # Tunic cap under the near shoulder.  The torso owns the adjacent scarf
        # and skirt pixels; only this rounded overlap follows the shoulder joint.
        draw.ellipse((202, 210, 239, 251), fill=outline)
        draw.ellipse((208, 216, 235, 247), fill=teal)
        draw.arc((211, 218, 233, 245), 190, 330, fill=teal_light, width=3)
        draw.ellipse((282, 232, 324, 271), fill=outline)
        draw.ellipse((287, 236, 320, 267), fill=skin)
        draw.arc((290, 238, 317, 263), 190, 325, fill=skin_light, width=4)
        draw.ellipse((247, 315, 286, 352), fill=outline)
        draw.ellipse((252, 319, 282, 348), fill=teal)
    elif part_name == "arm_far":
        # The flattened idle hides the forearm between upper arm and hand.  A
        # continuous arm is authored under the torso so rotation cannot leave a
        # detached hand on a dark shard.
        draw.polygon([(339, 216), (371, 216), (388, 302), (398, 326),
                      (368, 340), (354, 304), (347, 260)], fill=outline)
        draw.polygon([(344, 221), (367, 221), (376, 278), (359, 288),
                      (352, 258)], fill=skin)
        draw.arc((346, 223, 366, 250), 190, 325, fill=skin_light, width=4)
        draw.polygon([(357, 277), (378, 271), (390, 318), (369, 329)],
                     fill=(105, 58, 31, 255))
        draw.line([(360, 285), (382, 280)], fill=(167, 92, 45, 255), width=4)

    allowed = ImageChops.multiply(patch.getchannel("A"), higher_opaque)
    result = image.copy()
    result.paste(patch, (0, 0), allowed)
    return result


def make_weapon(combat: Image.Image) -> Image.Image:
    """Recover the spear and bridge the two hand-occluded shaft sections."""
    result = Image.new("RGBA", combat.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(result)

    # Hand-hidden shaft: palette sampled from visible v2 spear pixels.
    draw.line([(48, 349), (354, 258)], fill=(31, 20, 13, 255), width=20)
    draw.line([(49, 346), (354, 255)], fill=(112, 58, 26, 255), width=13)
    draw.line([(51, 342), (351, 254)], fill=(174, 91, 39, 255), width=4)

    # Paste the original visible shaft, binding, and blade over the bridge.
    weapon_mask = Image.new("L", combat.size, 0)
    weapon_draw = ImageDraw.Draw(weapon_mask)
    weapon_draw.polygon([(42, 333), (348, 244), (360, 271), (50, 363)], fill=255)
    weapon_draw.polygon([(337, 232), (432, 223), (396, 279), (344, 282)], fill=255)

    pixels = combat.load()
    selected = weapon_mask.load()
    filtered = Image.new("L", combat.size, 0)
    filtered_pixels = filtered.load()
    for y in range(combat.height):
        for x in range(combat.width):
            if selected[x, y] == 0:
                continue
            red, green, blue, alpha = pixels[x, y]
            if alpha == 0:
                continue
            is_wood = red >= 55 and green <= 125 and blue <= 75 and red > green * 1.15
            is_metal = max(red, green, blue) - min(red, green, blue) <= 42 and red >= 55
            is_outline = red <= 55 and green <= 52 and blue <= 48
            if is_wood or is_metal or is_outline:
                filtered_pixels[x, y] = alpha
    result.paste(combat, (0, 0), filtered)
    return result


def compose(parts: list[dict], out_dir: Path, include_weapon: bool = False) -> Image.Image:
    canvas = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
    for part in sorted(parts, key=lambda item: item["z_index"]):
        if part["name"] == "weapon" and not include_weapon:
            continue
        image = Image.open(out_dir / part["file"]).convert("RGBA")
        canvas.alpha_composite(image, tuple(part["rest_position"]))
    return canvas


def compose_with_rotation(parts: list[dict], out_dir: Path, part_name: str, angle: float) -> Image.Image:
    """Compose one deterministic joint probe around the declared source pivot."""
    canvas = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
    for part in sorted(parts, key=lambda item: item["z_index"]):
        if part["name"] == "weapon":
            continue
        image = Image.open(out_dir / part["file"]).convert("RGBA")
        full = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
        full.alpha_composite(image, tuple(part["rest_position"]))
        if part["name"] == part_name:
            origin_x, origin_y = part["rest_position"]
            pivot = (origin_x + part["pivot"][0], origin_y + part["pivot"][1])
            full = full.rotate(angle, resample=Image.Resampling.NEAREST, center=pivot)
        canvas.alpha_composite(full)
    return canvas


def joint_check_sheet(parts: list[dict], out_dir: Path) -> Image.Image:
    """Render the minimum review angles that previously exposed bad cut lines."""
    probes = [
        ("arm_near", -15), ("arm_near", -10), ("arm_near", 15),
        ("arm_far", 10), ("leg_near", -10), ("leg_near", 10),
    ]
    background = (48, 44, 54, 255)
    panel_width, panel_height, header = 512, 556, 44
    sheet = Image.new("RGBA", (panel_width * 3, panel_height * 2), background)
    draw = ImageDraw.Draw(sheet)
    font = ImageFont.load_default()
    for index, (part_name, angle) in enumerate(probes):
        x = (index % 3) * panel_width
        y = (index // 3) * panel_height
        draw.text((x + 18, y + 16), f"{part_name} {angle:+d} deg", font=font,
                  fill=(240, 236, 220, 255))
        sheet.alpha_composite(compose_with_rotation(parts, out_dir, part_name, angle), (x, y + header))
    return sheet


def contact_sheet(reference: Image.Image, reconstructed: Image.Image, parts: list[dict], out_dir: Path) -> Image.Image:
    background = (48, 44, 54, 255)
    panel_width = 560
    header = 44
    sheet = Image.new("RGBA", (panel_width * 3, 600), background)
    font = ImageFont.load_default()
    draw = ImageDraw.Draw(sheet)

    for index, (label, image) in enumerate([
        ("v2 idle source cell", reference),
        ("cutout reconstructed idle", reconstructed),
    ]):
        x = index * panel_width + 24
        draw.text((x, 16), label, font=font, fill=(240, 236, 220, 255))
        sheet.alpha_composite(image, (index * panel_width + 24, header))

    exploded_x = panel_width * 2
    draw.text((exploded_x + 24, 16), "separate source-resolution parts", font=font, fill=(240, 236, 220, 255))
    cursor_x, cursor_y, row_height = exploded_x + 16, 62, 0
    for part in sorted(parts, key=lambda item: item["z_index"]):
        image = Image.open(out_dir / part["file"]).convert("RGBA")
        preview = image.copy()
        preview.thumbnail((160, 170), Image.Resampling.LANCZOS)
        needed_width = max(160, preview.width)
        if cursor_x + needed_width > sheet.width - 12:
            cursor_x = exploded_x + 16
            cursor_y += row_height + 34
            row_height = 0
        sheet.alpha_composite(preview, (cursor_x, cursor_y))
        draw.text((cursor_x, cursor_y + preview.height + 4), part["name"], font=font, fill=(226, 219, 191, 255))
        cursor_x += needed_width + 12
        row_height = max(row_height, preview.height)
    return sheet


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--alpha-sheet", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--contact-sheet", required=True, type=Path)
    parser.add_argument("--joint-check", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    args = parser.parse_args()

    sheet = Image.open(args.alpha_sheet).convert("RGBA")
    if sheet.size != EXPECTED_ALPHA_SIZE:
        raise ValueError(f"expected {EXPECTED_ALPHA_SIZE}, got {sheet.size}")
    idle = sheet.crop(IDLE_BOX)
    combat = sheet.crop(COMBAT_BOX)
    args.out_dir.mkdir(parents=True, exist_ok=True)
    args.contact_sheet.parent.mkdir(parents=True, exist_ok=True)
    args.joint_check.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.parent.mkdir(parents=True, exist_ok=True)

    built_parts: list[dict] = []
    coverage = Image.new("L", idle.size, 0)
    # Polygons follow semantic cut lines.  Expanding every polygon used to let
    # foreground limbs steal adjacent belt/scarf/tunic pixels; those pixels then
    # rotated with the limb and opened long holes.  Joint overlap is added below
    # explicitly and cannot change ownership away from the torso.
    authored_masks = {spec["name"]: polygon_mask(spec["polygon"]) for spec in PARTS}
    assigned = Image.new("L", idle.size, 0)
    part_masks: dict[str, Image.Image] = {}
    for spec in sorted(PARTS, key=lambda item: item["z_index"], reverse=True):
        available = ImageChops.invert(assigned)
        part_masks[spec["name"]] = ImageChops.multiply(authored_masks[spec["name"]], available)
        assigned = ImageChops.lighter(assigned, authored_masks[spec["name"]])

    near_pixels = part_masks["arm_near"].load()
    for y in range(247, 512):
        for x in range(217, 512):
            near_pixels[x, y] = 0
    far_pixels = part_masks["arm_far"].load()
    for y in range(320, 512):
        for x in range(0, 360):
            far_pixels[x, y] = 0
    nearest_assignments = assign_visible_pixels_by_nearest_part(part_masks, idle.getchannel("A"))

    # Duplicate only fully opaque source pixels around each joint.  These are
    # hidden by identical pixels in the rest pose, but give both adjoining
    # pieces real source material under a small rotation.
    opaque = idle.getchannel("A").point(lambda value: 255 if value == 255 else 0)

    for spec in PARTS:
        mask = part_masks[spec["name"]]
        joint = Image.new("L", idle.size, 0)
        joint_draw = ImageDraw.Draw(joint)
        px, py = spec["pivot_source"]
        joint_draw.ellipse((px - 14, py - 14, px + 14, py + 14), fill=255)
        mask = ImageChops.lighter(mask, ImageChops.multiply(joint, opaque))
        coverage = ImageChops.lighter(coverage, mask)
        full = source_pixels(idle, mask)
        higher = Image.new("L", idle.size, 0)
        for other in PARTS:
            if other["z_index"] > spec["z_index"]:
                higher = ImageChops.lighter(higher, part_masks[other["name"]])
        higher_opaque = ImageChops.multiply(higher, opaque)
        full = add_hidden_joint_fill(spec["name"], full, higher_opaque)
        cropped, origin = crop_with_padding(full)
        components = significant_alpha_components(cropped)
        if len(components) != 1:
            raise RuntimeError(
                f"{spec['name']} has {len(components)} significant alpha components: {components}"
            )
        filename = f"goblin_cutout_{spec['name']}_v1.png"
        cropped.save(args.out_dir / filename, optimize=True)
        pivot = [spec["pivot_source"][0] - origin[0], spec["pivot_source"][1] - origin[1]]
        built_parts.append({
            "name": spec["name"],
            "file": filename,
            "pivot": pivot,
            "parent": spec["parent"],
            "z_index": spec["z_index"],
            "rest_position": list(origin),
            "motion": spec["motion"],
            "significant_alpha_components": len(components),
        })

    weapon_full = make_weapon(combat)
    weapon_crop, weapon_origin = crop_with_padding(weapon_full)
    weapon_file = "goblin_cutout_weapon_spear_v1.png"
    weapon_crop.save(args.out_dir / weapon_file, optimize=True)
    built_parts.append({
        "name": "weapon",
        "file": weapon_file,
        "pivot": [279 - weapon_origin[0], 278 - weapon_origin[1]],
        "parent": "arm_near",
        "z_index": 6,
        "rest_position": list(weapon_origin),
        "visible_in_rest": False,
        "motion": "Separate equipment layer follows the strike hand and can be swapped without redrawing the arm.",
    })

    metadata = {
        "format_version": 1,
        "coordinate_space": "source-cell pixels; origin is top-left; pivots are local to each PNG",
        "source_cell_size": [512, 512],
        "source_body_bbox": list(SOURCE_BODY_BBOX),
        "runtime_target_size": [116, 168],
        "rest_pose": "idle; weapon is intentionally hidden",
        "parts": built_parts,
    }
    metadata_path = args.out_dir / "goblin_cutout_rig_v1.json"
    metadata_path.write_text(json.dumps(metadata, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    reconstructed = compose(built_parts, args.out_dir)
    source_rgba = idle
    if reconstructed.tobytes() != source_rgba.tobytes():
        diff = sum(a != b for a, b in zip(reconstructed.tobytes(), source_rgba.tobytes()))
        raise RuntimeError(f"rest reconstruction differs from source in {diff} channel bytes")
    contact_sheet(source_rgba, reconstructed, built_parts, args.out_dir).save(args.contact_sheet, optimize=True)
    joint_check_sheet(built_parts, args.out_dir).save(args.joint_check, optimize=True)

    manifest = {
        "builder": {
            "path": "evidence/243-build-goblin-cutout.py",
            "committed_sha256": committed_sha256(Path(__file__)),
        },
        "alpha_sheet": {"path": args.alpha_sheet.as_posix(), "sha256": sha256(args.alpha_sheet)},
        "nearest_visible_edge_assignments": nearest_assignments,
        "rest_reconstruction": "byte-identical RGBA to idle source cell",
        "outputs": [
            {"path": path.as_posix(), "committed_sha256": committed_sha256(path)}
            for path in sorted([*args.out_dir.glob("goblin_cutout_*_v1.png"), metadata_path,
                                args.contact_sheet, args.joint_check])
        ],
    }
    args.manifest.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
