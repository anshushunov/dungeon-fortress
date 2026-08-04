#!/usr/bin/env python3
"""Build the Issue #243 goblin cutout from the v2 alpha sprite sheet.

The input is the 1536x1024 alpha sheet produced by the repository's
scripts/art/remove_chroma_key.py.  Body pixels come from the idle cell; the
separate spear comes from the combat cell.  All coordinates below are in the
512x512 source-cell coordinate system.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont


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
        "polygon": [(286, 302), (352, 300), (390, 365), (385, 412), (304, 412), (290, 356)],
        "motion": "Independent far-leg step and crouch during windup and recovery.",
    },
    {
        "name": "arm_far",
        "parent": "torso",
        "z_index": 1,
        "pivot_source": (352, 236),
        "polygon": [(326, 205), (375, 207), (403, 276), (397, 349), (348, 357), (330, 304)],
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
        "polygon": [(199, 301), (294, 300), (316, 356), (304, 425), (197, 429), (190, 365)],
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
        "polygon": [(184, 194), (233, 199), (249, 248), (239, 305), (239, 353), (183, 363),
                    (169, 310), (173, 257)],
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


def polygon_mask(points: list[tuple[int, int]], size: tuple[int, int] = (512, 512)) -> Image.Image:
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    return mask


def source_pixels(source: Image.Image, mask: Image.Image) -> Image.Image:
    result = source.copy()
    alpha = ImageChops.multiply(source.getchannel("A"), mask)
    result.putalpha(alpha)
    return result


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

    if part_name == "torso":
        # Tunic under the near shoulder, neck under the head, and cloth over the
        # near hip.  These zones are absent from the flattened source because
        # foreground parts cover them.
        draw.ellipse((200, 212, 241, 256), fill=outline)
        draw.ellipse((205, 217, 238, 253), fill=teal)
        draw.arc((208, 219, 237, 249), 190, 330, fill=teal_light, width=4)
        draw.ellipse((282, 232, 324, 271), fill=outline)
        draw.ellipse((287, 236, 320, 267), fill=skin)
        draw.arc((290, 238, 317, 263), 190, 325, fill=skin_light, width=4)
        draw.ellipse((247, 315, 286, 352), fill=outline)
        draw.ellipse((252, 319, 282, 348), fill=teal)
    elif part_name == "arm_far":
        # Round upper-arm cap under the torso for the far-arm counter-swing.
        draw.ellipse((334, 216, 371, 254), fill=outline)
        draw.ellipse((339, 220, 367, 250), fill=skin)
        draw.arc((342, 222, 365, 247), 190, 325, fill=skin_light, width=4)

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
    parser.add_argument("--manifest", required=True, type=Path)
    args = parser.parse_args()

    sheet = Image.open(args.alpha_sheet).convert("RGBA")
    if sheet.size != EXPECTED_ALPHA_SIZE:
        raise ValueError(f"expected {EXPECTED_ALPHA_SIZE}, got {sheet.size}")
    idle = sheet.crop(IDLE_BOX)
    combat = sheet.crop(COMBAT_BOX)
    args.out_dir.mkdir(parents=True, exist_ok=True)
    args.contact_sheet.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.parent.mkdir(parents=True, exist_ok=True)

    built_parts: list[dict] = []
    coverage = Image.new("L", idle.size, 0)
    authored_masks = {
        spec["name"]: polygon_mask(spec["polygon"]).filter(ImageFilter.MaxFilter(17))
        for spec in PARTS
    }
    assigned = Image.new("L", idle.size, 0)
    part_masks: dict[str, Image.Image] = {}
    for spec in sorted(PARTS, key=lambda item: item["z_index"], reverse=True):
        available = ImageChops.invert(assigned)
        part_masks[spec["name"]] = ImageChops.multiply(authored_masks[spec["name"]], available)
        assigned = ImageChops.lighter(assigned, authored_masks[spec["name"]])

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
        })

    # Any visible idle pixel missed by the authored polygons belongs to torso.
    missed = Image.new("L", idle.size, 0)
    idle_alpha = idle.getchannel("A")
    missed_pixels = missed.load()
    coverage_pixels = coverage.load()
    alpha_pixels = idle_alpha.load()
    missed_count = 0
    for y in range(idle.height):
        for x in range(idle.width):
            if alpha_pixels[x, y] > 0 and coverage_pixels[x, y] == 0:
                missed_pixels[x, y] = 255
                missed_count += 1
    if missed_count:
        torso_meta = next(part for part in built_parts if part["name"] == "torso")
        torso_path = args.out_dir / torso_meta["file"]
        torso_crop = Image.open(torso_path).convert("RGBA")
        full_torso = Image.new("RGBA", idle.size, (0, 0, 0, 0))
        full_torso.alpha_composite(torso_crop, tuple(torso_meta["rest_position"]))
        full_torso.paste(idle, (0, 0), missed)
        recropped, origin = crop_with_padding(full_torso)
        recropped.save(torso_path, optimize=True)
        torso_meta["rest_position"] = list(origin)
        torso_meta["pivot"] = [286 - origin[0], 330 - origin[1]]

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

    manifest = {
        "builder": {
            "path": "evidence/243-build-goblin-cutout.py",
            "committed_sha256": committed_sha256(Path(__file__)),
        },
        "alpha_sheet": {"path": args.alpha_sheet.as_posix(), "sha256": sha256(args.alpha_sheet)},
        "missed_visible_pixels_assigned_to_torso": missed_count,
        "rest_reconstruction": "byte-identical RGBA to idle source cell",
        "outputs": [
            {"path": path.as_posix(), "committed_sha256": committed_sha256(path)}
            for path in sorted([*args.out_dir.glob("goblin_cutout_*_v1.png"), metadata_path, args.contact_sheet])
        ],
    }
    args.manifest.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
