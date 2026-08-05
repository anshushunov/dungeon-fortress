"""Measure how much of a blow an eye can see at the zoom the game is played at.

Why this exists
---------------
The owner played ADR 0020's duel scene twice. After Issue #244 he said «на демке
плохо видно, но движения в целом ок», and the independent review of PR #256 had
said the same thing before him: «размах руки читается, силуэт тела почти нет».
After Issue #259 he was sharper: «нет плавности движения, очень быстрые удары даже
на скорости 0.5 (должны быть сильно медленнее), не хватает кадров как будто,
просто две позы».

All of those are claims about a picture. This turns them into numbers, so that the
decision the owner then took — combat gets a ceiling, readable rather than
beautiful, and the visual branch is put down — rests on a measurement rather than
on an impression, and so that whoever comes back to it has the arithmetic.

**Nothing here changes the game.** The chain it measures is the chain the
repository ships, transcribed into `evidence/263-chain-shipped.json`, and
`evidence/244-measure-rig-gaps.py` is imported rather than edited.

What is measured
----------------
*Amplitude.* The rig is composited exactly as `244-measure-rig-gaps.py` composites
it and then put through the rest of what `Main.PushBodyPose` does to a body: the
whole-frame lean of `StrikeChain.LeanDegrees`, the squash and stretch of
`BodyMotion.BlowHeightScale`, and the throw of `StrikeChain.RecoilOffsetRef`. The
result is downsampled to the size a body has on screen at the working zoom and
compared with the same body standing still. `changedPx` is that difference, and it
is reported for three groups, because the review's sentence is a statement about
two of them:

* `whole` — every part, weapon included: the whole figure;
* `body`  — everything except the striking arm and its spear;
* `arm`   — the striking arm and its spear alone.

`PosePx` is the same difference with the lean, the throw and the stretch taken out,
i.e. how much the figure changes *shape* rather than place. The gap between the two
is the whole of «силуэт тела почти не меняется»: a body that slides and tilts
rigidly has not changed shape however many pixels moved.

*Time.* A blow is one tick. At `Main.TicksPerSecond = 6.0` that is 167 ms at speed
1 and 333 ms at speed 0.5, drawn in ten and twenty frames of a 60 Hz display.
`--timing` walks those frames and reports how many of them carry the movement,
which is «просто две позы» as a count, and `--budget` sets the result against what
a melee swing is normally given.

*Seams.* `--gaps` walks the interpolated chain rather than its five keyframes and
reports the worst `slit_pixels` anywhere on it. `--sweep-wide` asks how far each
part could be turned if anyone wanted to, and `--rigid` shows why the root's own
number in that sweep cannot be read as a seam.

Usage
-----
    python evidence/263-measure-strike-readability.py --readability --timing \
        --budget --gaps --rigid --sweep-wide --json evidence/263-measurement.json
    python evidence/263-measure-strike-readability.py --check-chain
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SHIPPED_CHAIN = os.path.join(HERE, "263-chain-shipped.json")


def _load_rig_gaps():
    """The measurement of Issue #244, imported rather than re-typed.

    Its file name starts with a digit, so it is not an importable module name;
    the loader below is what makes "import it, do not copy it" possible at all.
    Nothing in that file is modified by this one: Issue #263 measures the chain
    the repository ships and does not touch the angles, so the copy of them that
    `244-measure-rig-gaps.py` carries stays exactly as it was.
    """
    path = os.path.join(HERE, "244-measure-rig-gaps.py")
    spec = importlib.util.spec_from_file_location("rig_gaps_244", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


GAPS = _load_rig_gaps()

# ---------------------------------------------------------------------------
# The picture the owner is looking at, in the numbers that decide its size.
#
# CameraView.GoblinDrawSize(40) = 20 * 1.70 * (40/22) = 61.818 world px is the
# height of a body's whole 272x192 canvas at the shipped tile; BodyRig maps the
# rig's 330-source-pixel body onto 168 of those 192 rows. At camera zoom 1.0 a
# world pixel is a screen pixel, so the goblin an owner sees while playing the
# map is 54.1 px tall. `--demo-duel` forces zoom 2.0 — the largest declared
# level — which is why its frames flatter the animation: everything below is
# measured at 1.0.
# ---------------------------------------------------------------------------
REFERENCE_GOBLIN_DRAW_SIZE = 20.0
BODY_VISUAL_SCALE = 1.70
TILE_SIZE = 40
WORLD_VISUAL_SCALE = TILE_SIZE / 22.0
GOBLIN_DRAW_SIZE = REFERENCE_GOBLIN_DRAW_SIZE * BODY_VISUAL_SCALE * WORLD_VISUAL_SCALE
SPRITE_CANVAS_HEIGHT = 192.0
WORKING_ZOOM = 1.0

# Main.TicksPerSecond, and the display the owner plays on.
TICKS_PER_SECOND = 6.0
MILLISECONDS_PER_TICK = 1000.0 / TICKS_PER_SECOND
DISPLAY_HZ = 60.0
FRAME_MILLISECONDS = 1000.0 / DISPLAY_HZ
WATCHED_SPEEDS = (1.0, 0.5)

# What a melee swing is normally given, in milliseconds, as the brief of Issue
# #263 states it: an anticipation of 150-250, then contact, then a recovery of
# 200-300, and 500-800 for the whole exchange. A reference point, not a target
# this repository has adopted.
NORM_WINDUP_MS = (150.0, 250.0)
NORM_RECOVERY_MS = (200.0, 300.0)
NORM_EXCHANGE_MS = (500.0, 800.0)

# BodyMotion.StretchPeak / StretchFloor and SquashPeak / SquashFloor.
STRETCH_PEAK, STRETCH_FLOOR = 0.12, 0.07
SQUASH_PEAK, SQUASH_FLOOR = 0.16, 0.10

# A frame is "held" when the shape is changing at no more than this share of the
# fastest frame of the same chain. A quarter is a wide reading of "the picture
# dwells here" on purpose: the claim this number carries is that a chain of
# straight lines dwells nowhere except its two ends, and a wide threshold makes
# that harder to claim, not easier.
HELD_SHARE = 0.25

STRIKE_ARM = ("arm_near", "weapon")
ALL_PARTS = (
    "leg_far",
    "arm_far",
    "torso",
    "leg_near",
    "head",
    "arm_near",
    "weapon",
)
GROUPS = {
    "whole": ALL_PARTS,
    "body": tuple(name for name in ALL_PARTS if name not in STRIKE_ARM),
    "arm": STRIKE_ARM,
}


# ---------------------------------------------------------------------------
# The chain, as data.
# ---------------------------------------------------------------------------
class Key:
    """One keyframe of one chain, exactly as `StrikeChain.cs` states it."""

    def __init__(self, entry: dict):
        self.at = entry["at"]
        self.label = entry["label"]
        self.lean = entry["lean"]
        self.recoil = entry["recoil"]
        self.angles = entry.get("angles", {})
        self.offsets = {
            part: tuple(value) for part, value in entry.get("offsets", {}).items()
        }


def load_chain(path: str) -> tuple[list[Key], list[Key], str]:
    """The shipped chain, read out of a file rather than retyped here.

    `StrikeChain.cs` is the source of truth and this is a transcription of it,
    kept as data with its source named inside the file. Issue #263 changes no
    angle, so the transcription cannot drift within this task; the standing risk
    that it drifts later is the one `docs/engineering/DEBT_LEDGER.md` already
    carries for the copy inside `244-measure-rig-gaps.py`.
    """
    with open(path, "r", encoding="utf-8") as handle:
        document = json.load(handle)
    return (
        [Key(entry) for entry in document["attacker"]],
        [Key(entry) for entry in document["target"]],
        document.get("interpolation", "linear"),
    )


ATTACKER_CHAIN, TARGET_CHAIN, INTERPOLATION = load_chain(SHIPPED_CHAIN)


def span(chain: list[Key], alpha: float) -> tuple[Key, Key, float]:
    """`StrikeChain.Span`: the two keyframes around a moment, and how far between."""
    alpha = min(max(alpha, 0.0), 1.0)
    for index in range(1, len(chain)):
        if alpha <= chain[index].at:
            before, after = chain[index - 1], chain[index]
            width = after.at - before.at
            return before, after, 1.0 if width <= 0.0 else (alpha - before.at) / width
    return chain[-1], chain[-1], 1.0


def _lerp(before: float, after: float, share: float) -> float:
    return before + ((after - before) * share)


def pose_at(chain: list[Key], alpha: float) -> tuple:
    """The whole pose at one moment: angles, offsets, lean and throw.

    `StrikeChain.PoseOf`, `LeanDegrees` and `RecoilOffsetRef` answered together,
    because they are one keyframe row read three ways. Straight lines between
    keyframes, which is what the shipped runtime plays — and what the speed
    profile of `--timing` is a measurement of.
    """
    before, after, share = span(chain, alpha)
    parts = sorted(
        set(before.angles) | set(after.angles) |
        set(before.offsets) | set(after.offsets))
    angles = {
        name: _lerp(before.angles.get(name, 0.0), after.angles.get(name, 0.0), share)
        for name in parts
    }
    offsets = {}
    for name in parts:
        bx, by = before.offsets.get(name, (0.0, 0.0))
        ax, ay = after.offsets.get(name, (0.0, 0.0))
        offsets[name] = (_lerp(bx, ax, share), _lerp(by, ay, share))
    return (
        angles,
        offsets,
        _lerp(before.lean, after.lean, share),
        _lerp(before.recoil, after.recoil, share),
    )


def role_of(chain: list[Key]) -> str:
    return "attacker" if chain is ATTACKER_CHAIN else "target"


# ---------------------------------------------------------------------------
# Composing a body.
# ---------------------------------------------------------------------------
def compose_parts(
    rig: dict,
    rig_dir: str,
    parts: tuple[str, ...],
    angles: dict[str, float],
    offsets: dict[str, tuple[float, float]],
) -> Image.Image:
    """The rig posed, with only some of its parts painted.

    `GAPS.compose` has no part filter and Issue #263 must not add one to it, so
    the draw loop is here — built out of that module's own transform helpers, so
    the geometry is still written once. Whole parts are dropped and never
    repainted: a group is the same layers in the same places with some left out,
    and `body` plus `arm` is `whole` by construction.
    """
    if set(parts) == set(ALL_PARTS):
        return GAPS.compose(
            rig, rig_dir, angles=angles, offsets=offsets, show_weapon=True)

    size = tuple(rig["source_cell_size"])
    by_name = {part["name"]: part for part in rig["parts"]}
    transforms: dict[str, tuple[float, ...]] = {}

    def transform_of(name: str) -> tuple[float, ...]:
        if name in transforms:
            return transforms[name]
        part = by_name[name]
        joint = (
            part["rest_position"][0] + part["pivot"][0],
            part["rest_position"][1] + part["pivot"][1],
        )
        own = GAPS.rotate_about(joint, angles.get(name, 0.0))
        dx, dy = offsets.get(name, (0.0, 0.0))
        own = GAPS.multiply(GAPS.translate(dx, dy), own)
        parent = part["parent"]
        total = own if parent is None else GAPS.multiply(transform_of(parent), own)
        transforms[name] = total
        return total

    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    for part in sorted(rig["parts"], key=lambda item: item["z_index"]):
        if part["name"] not in parts:
            continue
        image = Image.open(os.path.join(rig_dir, part["file"])).convert("RGBA")
        placement = GAPS.multiply(
            transform_of(part["name"]),
            GAPS.translate(part["rest_position"][0], part["rest_position"][1]),
        )
        canvas = Image.alpha_composite(
            canvas,
            image.transform(
                size, Image.AFFINE, GAPS.invert(placement), resample=Image.NEAREST),
        )
    return canvas


def source_to_world(rig: dict) -> float:
    canvas = GOBLIN_DRAW_SIZE / SPRITE_CANVAS_HEIGHT
    return GAPS.runtime_scale(rig) * canvas * WORKING_ZOOM


def foot_point(rig: dict) -> tuple[float, float]:
    """Where the body stands, in source-cell pixels.

    `Main.PushBodyPose` puts the origin of a body's frame on its feet, so the lean
    and the squash turn and stretch it about that point and not about its middle.
    """
    x0, _, x1, y1 = rig["source_body_bbox"]
    return ((x0 + x1) / 2.0, float(y1))


def scale_about(pivot: tuple[float, float], sx: float, sy: float) -> tuple[float, ...]:
    px, py = pivot
    return (sx, 0.0, px - sx * px, 0.0, sy, py - sy * py)


def blow_height_scale(role: str, alpha: float) -> float:
    """`BodyMotion.BlowHeightScale`, for the two roles that have one."""
    alpha = min(max(alpha, 0.0), 1.0)
    if role == "attacker":
        return 1.0 + STRETCH_PEAK + ((STRETCH_FLOOR - STRETCH_PEAK) * alpha)
    if role == "target":
        return 1.0 / (1.0 + SQUASH_PEAK + ((SQUASH_FLOOR - SQUASH_PEAK) * alpha))
    return 1.0


def drawn_body(
    rig: dict,
    rig_dir: str,
    parts: tuple[str, ...],
    angles: dict[str, float],
    offsets: dict[str, tuple[float, float]],
    lean_degrees: float,
    recoil_ref: float,
    height_scale: float,
) -> Image.Image:
    """One posed body as the screen gets it, still in the 512x512 source cell.

    Scale first, then the lean, then the throw: the order
    `Transform2D(rotation, scale, skew, origin)` composes them in.
    """
    body = compose_parts(rig, rig_dir, parts, angles, offsets)
    scale = source_to_world(rig)
    pivot = foot_point(rig)
    frame = scale_about(pivot, 1.0 / height_scale, height_scale)
    frame = GAPS.multiply(GAPS.rotate_about(pivot, lean_degrees), frame)
    frame = GAPS.multiply(
        GAPS.translate(recoil_ref * WORLD_VISUAL_SCALE / scale, 0.0), frame)
    return body.transform(
        body.size, Image.AFFINE, GAPS.invert(frame), resample=Image.BILINEAR)


def to_screen(image: Image.Image, scale: float) -> Image.Image:
    return image.resize(
        (
            max(1, int(round(image.size[0] * scale))),
            max(1, int(round(image.size[1] * scale))),
        ),
        Image.LANCZOS,
    )


def silhouette(image: Image.Image) -> list[list[bool]]:
    width, height = image.size
    data = list(image.getchannel("A").getdata())
    return [[data[y * width + x] >= 128 for x in range(width)] for y in range(height)]


def changed_pixels(rest: Image.Image, posed: Image.Image) -> int:
    """Screen pixels whose silhouette differs — the animation an eye can see."""
    before = silhouette(rest)
    after = silhouette(posed)
    return sum(
        1
        for y in range(len(before))
        for x in range(len(before[0]))
        if before[y][x] != after[y][x]
    )


def area(image: Image.Image) -> int:
    return sum(sum(1 for pixel in row if pixel) for row in silhouette(image))


# ---------------------------------------------------------------------------
# The measurements.
# ---------------------------------------------------------------------------
def _frames(rig, rig_dir, chain, alpha, scale, frame_motion=True):
    angles, offsets, lean, recoil = pose_at(chain, alpha)
    height = blow_height_scale(role_of(chain), alpha)
    if not frame_motion:
        lean, recoil, height = 0.0, 0.0, 1.0
    return {
        group: to_screen(
            drawn_body(rig, rig_dir, parts, angles, offsets, lean, recoil, height),
            scale,
        )
        for group, parts in GROUPS.items()
    }


def readability(rig, rig_dir, steps: int) -> list[dict]:
    """The chain sampled at the twelfths `run-game.ps1 -DuelFrame` steps through.

    Two baselines, because "плохо видно" can mean two different things and only
    one of them is the animation:

    * `ChangedPx` is against the **first frame of this body's own tick** — the
      body as it stood when the blow started, squash and stretch already applied.
      This is what moves while the blow is drawn.
    * `VsStancePx` is against a body no blow touched at all. It also counts the
      constant part: `BodyMotion.BlowHeightScale` never returns to 1 within the
      tick, so a striking body is 7-12 % taller for the whole of it.

    And `PosePx` is the shape alone, with every rigid motion of the frame removed.
    """
    scale = source_to_world(rig)
    rows = []
    stance = {
        group: to_screen(drawn_body(rig, rig_dir, parts, {}, {}, 0.0, 0.0, 1.0), scale)
        for group, parts in GROUPS.items()
    }
    for chain, name in ((ATTACKER_CHAIN, "strike"), (TARGET_CHAIN, "flinch")):
        start = _frames(rig, rig_dir, chain, 0.0, scale)
        previous = start
        for step in range(steps + 1):
            alpha = step / steps
            angles, _, lean, recoil = pose_at(chain, alpha)
            drawn = _frames(rig, rig_dir, chain, alpha, scale)
            posed = _frames(rig, rig_dir, chain, alpha, scale, frame_motion=False)
            row = {
                "chain": name,
                "duelFrame": step,
                "alpha": round(alpha, 6),
                "leanDegrees": round(lean, 3),
                "recoilRef": round(recoil, 3),
                "armNearDegrees": round(angles.get("arm_near", 0.0), 3),
            }
            for group in GROUPS:
                row[group + "ChangedPx"] = changed_pixels(start[group], drawn[group])
                row[group + "VsStancePx"] = changed_pixels(stance[group], drawn[group])
                row[group + "PosePx"] = changed_pixels(stance[group], posed[group])
            row["wholeStepPx"] = changed_pixels(previous["whole"], drawn["whole"])
            rows.append(row)
            previous = drawn
    return rows


def timing(rig, rig_dir, thresholds=(0.05, 0.10)) -> dict:
    """How long a blow is on screen, and how many poses an eye gets out of it.

    A blow is one tick, so its length on screen is the length of a tick and
    nothing about the chain can change it — that is reported rather than derived.
    What the chain does decide is how many of the frames drawn in that tick differ
    from each other enough to read as separate poses, which is «просто две позы»
    stated as a count.
    """
    scale = source_to_world(rig)
    stance_area = area(
        to_screen(drawn_body(rig, rig_dir, ALL_PARTS, {}, {}, 0.0, 0.0, 1.0), scale))
    result = {"stanceAreaPx": stance_area, "speeds": []}
    for speed in WATCHED_SPEEDS:
        frames = DISPLAY_HZ / (TICKS_PER_SECOND * speed)
        entry = {
            "speed": speed,
            "tickMilliseconds": round(MILLISECONDS_PER_TICK / speed, 3),
            "displayFrames": round(frames, 3),
            "chains": [],
        }
        for chain, name in ((ATTACKER_CHAIN, "strike"), (TARGET_CHAIN, "flinch")):
            drawn, shapes = [], []
            for step in range(int(round(frames)) + 1):
                alpha = min(step / frames, 1.0)
                angles, offsets, lean, recoil = pose_at(chain, alpha)
                drawn.append(
                    to_screen(
                        drawn_body(
                            rig, rig_dir, ALL_PARTS, angles, offsets, lean, recoil,
                            blow_height_scale(role_of(chain), alpha)),
                        scale))
                # The same frame with the lean, the throw and the stretch taken
                # out. A body that only slides and tilts is one pose however far
                # it slides, and «просто две позы» is a claim about poses.
                shapes.append(
                    to_screen(
                        drawn_body(
                            rig, rig_dir, ALL_PARTS, angles, offsets, 0.0, 0.0, 1.0),
                        scale))
            travel = sum(
                changed_pixels(drawn[index - 1], drawn[index])
                for index in range(1, len(drawn)))
            counts = {}
            for share in thresholds:
                limit = share * stance_area
                for label, series in (("Shape", shapes), ("Drawn", drawn)):
                    kept, last = 1, series[0]
                    for frame in series[1:]:
                        if changed_pixels(last, frame) > limit:
                            kept += 1
                            last = frame
                    counts[f"poses{label}At{int(share * 100)}Percent"] = kept

            # How fast the shape is changing, frame by frame. This is where the
            # three complaints separate: a pose an eye can read is one the picture
            # dwells on, and a movement that flows is one whose speed does not jump
            # between neighbouring frames.
            speeds = [
                changed_pixels(shapes[index - 1], shapes[index])
                for index in range(1, len(shapes))]
            peak = max(speeds) or 1
            held = [value <= HELD_SHARE * peak for value in speeds]
            held_poses = sum(
                1
                for index, value in enumerate(held)
                if value and (index == 0 or not held[index - 1]))
            jerk = max(
                abs(speeds[index] - speeds[index - 1])
                for index in range(1, len(speeds)))
            # How many drawn frames actually carry the movement. This is «просто
            # две позы» as a count: one frame above half the peak means the eye got
            # the pose before it and the pose after it and nothing in between.
            carrying = sum(1 for value in speeds if value >= peak / 2)
            entry["chains"].append(
                {
                    "chain": name,
                    "drawnFrames": len(drawn),
                    "silhouetteTravelPx": travel,
                    "shapeSpeedPx": speeds,
                    "peakShapeSpeedPx": peak,
                    "framesCarryingHalfPeak": carrying,
                    "heldPoses": held_poses,
                    "largestSpeedJumpPx": jerk,
                    "largestSpeedJumpShare": round(jerk / peak, 3),
                    **counts,
                })
            print(
                f"speed {speed}: {name} in {len(drawn)} frames over "
                f"{entry['tickMilliseconds']:.0f} ms, travel {travel} px, "
                f"carrying={carrying}, heldPoses={held_poses}, peakSpeed={peak}, "
                f"jump={jerk} ({jerk / peak:.0%} of peak), "
                + ", ".join(f"{key}={value}" for key, value in counts.items()))
            print("    speed profile: " + " ".join(str(value) for value in speeds))
        result["speeds"].append(entry)
    return result


def budget() -> dict:
    """The time a blow gets against the time a blow is normally given.

    Everything here is arithmetic on constants already in the repository —
    `Main.TicksPerSecond` and `StrikeChain.ContactShare` — set against the
    reference range named in the brief of Issue #263. It is computed rather than
    written in prose because the conclusion of this Issue turns on it.
    """
    contact_share = 0.35  # StrikeChain.ContactShare, which is BlowEffects.HitStopShare.
    rows = []
    for speed in WATCHED_SPEEDS:
        tick = MILLISECONDS_PER_TICK / speed
        windup = tick * contact_share
        recovery = tick * (1.0 - contact_share)
        rows.append(
            {
                "speed": speed,
                "exchangeMs": round(tick, 1),
                "windupMs": round(windup, 1),
                "recoveryMs": round(recovery, 1),
                "drawnFrames": round(DISPLAY_HZ / (TICKS_PER_SECOND * speed), 2),
                "exchangeShortfall": [
                    round(NORM_EXCHANGE_MS[0] / tick, 2),
                    round(NORM_EXCHANGE_MS[1] / tick, 2),
                ],
                "windupShortfall": [
                    round(NORM_WINDUP_MS[0] / windup, 2),
                    round(NORM_WINDUP_MS[1] / windup, 2),
                ],
                "recoveryShortfall": [
                    round(NORM_RECOVERY_MS[0] / recovery, 2),
                    round(NORM_RECOVERY_MS[1] / recovery, 2),
                ],
            })
        print(
            f"speed {speed}: the exchange gets {tick:.0f} ms "
            f"({windup:.0f} ms before contact, {recovery:.0f} ms after) against a "
            f"reference {NORM_EXCHANGE_MS[0]:.0f}-{NORM_EXCHANGE_MS[1]:.0f} ms: "
            f"short by {NORM_EXCHANGE_MS[0] / tick:.1f}-"
            f"{NORM_EXCHANGE_MS[1] / tick:.1f} times")
    return {
        "ticksPerSecond": TICKS_PER_SECOND,
        "contactShare": contact_share,
        "referenceWindupMs": list(NORM_WINDUP_MS),
        "referenceRecoveryMs": list(NORM_RECOVERY_MS),
        "referenceExchangeMs": list(NORM_EXCHANGE_MS),
        "speeds": rows,
    }


GAP_SAMPLES = 40


def gaps_along_chain(rig, rig_dir) -> list[dict]:
    """`slit_pixels` everywhere on the chain, not only at its keyframes.

    The grid is `GAP_SAMPLES` even intervals *plus every keyframe time*, so the
    worst value reported can never be lower than the worst keyframe: a grid that
    misses a keyframe by a thousandth reports a seam smaller than the one the
    chain actually reaches, which is the wrong direction for a reference point to
    err in. Nothing before Issue #263 had checked between the keyframes at all.
    """
    rest = GAPS.compose(rig, rig_dir)
    rows = []
    for chain, name in ((ATTACKER_CHAIN, "strike"), (TARGET_CHAIN, "flinch")):
        grid = sorted(
            {step / GAP_SAMPLES for step in range(GAP_SAMPLES + 1)} |
            {key.at for key in chain})
        for alpha in grid:
            angles, offsets, _, _ = pose_at(chain, alpha)
            posed = GAPS.compose(rig, rig_dir, angles=angles, offsets=offsets)
            rows.append(
                {
                    "chain": name,
                    "alpha": round(alpha, 6),
                    "slitPixels": GAPS.slit_pixels(rest, posed),
                    "holePixels": GAPS.hole_pixels(posed),
                })
    return rows


def sweep_wide(rig, rig_dir, angles_to_try) -> list[dict]:
    """How far each part could be turned, past where Issue #244 stopped looking.

    That Issue swept +-30 degrees and the chain it produced sits against that
    boundary on the near arm. Whether the amplitude of this rig is spent or merely
    unused is a question about angles nobody has measured, so this measures them.
    """
    rest = GAPS.compose(rig, rig_dir)
    rows = []
    for part in ("arm_near", "arm_far", "head", "leg_near", "leg_far"):
        for degrees in angles_to_try:
            posed = GAPS.compose(rig, rig_dir, angles={part: degrees})
            rows.append(
                {
                    "part": part,
                    "degrees": degrees,
                    "slitPixels": GAPS.slit_pixels(rest, posed),
                    "holePixels": GAPS.hole_pixels(posed),
                    "revealedPixels": GAPS.revealed_pixels(rest, posed),
                })
            print(
                f"{part:9s} {degrees:+4d} deg  slit={rows[-1]['slitPixels']:5d}  "
                f"revealed={rows[-1]['revealedPixels']:5d}")
    return rows


def rigid_rotation(rig, rig_dir, angles=(5, 15, 30)) -> list[dict]:
    """What the seam measurement reports for a body that only turned.

    The rig's root is `torso` and every other part hangs off it, so an angle on
    the root turns the whole figure rigidly: nothing moves relative to anything
    and no joint can open. `slit_pixels` nonetheless reports hundreds of pixels,
    because it compares the posed body with an *unturned* rest pose and a body
    that moved leaves the outline it had. Turning the rest pose by the same angle
    and comparing again gives zero.

    Worth writing down: it is why `StrikeChain.LeanDegrees` turns the drawing
    frame rather than the `torso` part, and it is a caveat on every `torso` row of
    `evidence/244-rig-gaps.json`.
    """
    rest = GAPS.compose(rig, rig_dir)
    joint = next(
        (
            part["rest_position"][0] + part["pivot"][0],
            part["rest_position"][1] + part["pivot"][1],
        )
        for part in rig["parts"]
        if part["name"] == "torso"
    )
    rows = []
    for degrees in angles:
        posed = GAPS.compose(rig, rig_dir, angles={"torso": degrees})
        turned = rest.transform(
            rest.size,
            Image.AFFINE,
            GAPS.invert(GAPS.rotate_about(joint, degrees)),
            resample=Image.NEAREST,
        )
        rows.append(
            {
                "degrees": degrees,
                "slitAgainstRestPose": GAPS.slit_pixels(rest, posed),
                "slitAgainstTheSameRestPoseTurned": GAPS.slit_pixels(turned, posed),
            })
        print(
            f"torso {degrees:+3d}: slit against the rest pose "
            f"{rows[-1]['slitAgainstRestPose']}, against the same rest pose turned "
            f"the same way {rows[-1]['slitAgainstTheSameRestPoseTurned']}")
    return rows


def contact_sheet(rig, rig_dir, path: str, steps: int) -> None:
    """The strike chain at the working zoom, one panel per twelfth of the tick."""
    scale = source_to_world(rig)
    panels = []
    for step in range(steps + 1):
        alpha = step / steps
        angles, offsets, lean, recoil = pose_at(ATTACKER_CHAIN, alpha)
        panels.append(
            to_screen(
                drawn_body(
                    rig, rig_dir, ALL_PARTS, angles, offsets, lean, recoil,
                    blow_height_scale("attacker", alpha)),
                scale))
    cell = panels[0].size
    sheet = Image.new("RGBA", (cell[0] * len(panels), cell[1]), (51, 47, 66, 255))
    for index, panel in enumerate(panels):
        sheet.alpha_composite(panel, (index * cell[0], 0))
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    sheet.save(path)
    print(f"sheet: {path}")


def check_chain(source: str, effects: str) -> bool:
    """Read `StrikeChain.cs` and confirm the transcription still matches it.

    The measurement of this Issue is only worth anything if the chain it plays is
    the chain the runtime plays, and «I typed it across carefully» is not a check.
    This parses the shipped keyframe table out of the C# — times, phase, lean,
    throw, five angles and five slides — and compares it with
    `263-chain-shipped.json` field by field. It is a *reader*: it never writes to
    the source, which is what keeps this Issue's promise not to touch the angles
    checkable rather than asserted.
    """
    import re

    text = open(source, "r", encoding="utf-8").read()
    hit_stop = float(
        re.search(
            r"const double HitStopShare = ([0-9.]+)",
            open(effects, "r", encoding="utf-8").read()).group(1))
    named = {"ContactShare": hit_stop}
    for name in ("FollowThroughShare", "RecoverShare", "ImpactShare", "SettleShare"):
        named[name] = float(
            re.search(r"const double " + name + r" = ([0-9.]+);", text).group(1))
    parts = ["head", "arm_near", "arm_far", "leg_near", "leg_far"]

    def parse(block: str) -> list[dict]:
        keys = []
        pattern = (r"new\(([^,]+), StrikePhase\.(\w+), ([-\d.]+), ([-\d.]+), "
                   r"(RestPose|Pose\((?:[^()]|\([^()]*\))*\))\)")
        for at, _phase, lean, recoil, pose in re.findall(pattern, block):
            if pose == "RestPose":
                angles, offsets = {}, {}
            else:
                numbers = [int(value) for value in re.findall(r"-?\d+", pose)]
                angles = {name: numbers[index * 3] for index, name in enumerate(parts)}
                offsets = {
                    name: [numbers[index * 3 + 1], numbers[index * 3 + 2]]
                    for index, name in enumerate(parts)
                }
            expression = at.strip()
            for name, value in named.items():
                expression = expression.replace(name, repr(value))
            keys.append(
                {
                    "at": round(eval(expression), 6),  # noqa: S307 - arithmetic on constants
                    "lean": float(lean),
                    "recoil": float(recoil),
                    "angles": angles,
                    "offsets": offsets,
                })
        return keys

    shipped = {
        "attacker": parse(text[text.index("AttackerChain ="):text.index("TargetChain =")]),
        "target": parse(
            text[text.index("TargetChain ="):text.index("/// Which end of the blow")]),
    }
    with open(SHIPPED_CHAIN, "r", encoding="utf-8") as handle:
        stated = json.load(handle)

    agreed = True
    for chain in ("attacker", "target"):
        left, right = shipped[chain], stated[chain]
        if len(left) != len(right):
            print(f"{chain}: {len(left)} keyframes in the source, {len(right)} stated")
            agreed = False
            continue
        for source_key, stated_key in zip(left, right):
            for field in ("at", "lean", "recoil"):
                if abs(source_key[field] - stated_key[field]) > 1e-9:
                    print(
                        f"{chain} at {source_key['at']}: {field} is "
                        f"{source_key[field]} in the source and {stated_key[field]} "
                        "in the transcription")
                    agreed = False
            if source_key["angles"] != dict(stated_key.get("angles", {})):
                print(f"{chain} at {source_key['at']}: the angles differ")
                agreed = False
            if source_key["offsets"] != {
                    name: list(value)
                    for name, value in stated_key.get("offsets", {}).items()}:
                print(f"{chain} at {source_key['at']}: the slides differ")
                agreed = False
        print(f"{chain}: {'matches' if agreed else 'DIFFERS FROM'} {source}")
    return agreed


# The rectangle of a 1280x720 duel capture the two bodies stand in, found by eye
# once and then fixed so every sheet crops the same world.
DUEL_CROP = (300, 180, 700, 440)


def frame_sheet(captures: list[str], path: str, reduce_to_working_zoom: bool) -> None:
    """A sheet out of frames the engine drew, cropped to the two bodies.

    `--demo-duel` forces the largest declared zoom, 2.0, and does not take
    `--camera-zoom` back — so a frame of the duel is always twice the size the map
    is played at. `reduce_to_working_zoom` halves the crop, which is the same
    geometry a zoom of 1.0 gives; it is not the same resampling, and it is
    labelled as a reduction rather than as a capture for that reason.
    """
    panels = [Image.open(name).crop(DUEL_CROP) for name in captures]
    if reduce_to_working_zoom:
        panels = [
            panel.resize((panel.width // 2, panel.height // 2), Image.LANCZOS)
            for panel in panels
        ]
    columns = 3
    cell = panels[0].size
    rows = (len(panels) + columns - 1) // columns
    sheet = Image.new(
        "RGB", (cell[0] * columns, cell[1] * rows), (51, 47, 66))
    for index, panel in enumerate(panels):
        sheet.paste(
            panel.convert("RGB"),
            ((index % columns) * cell[0], (index // columns) * cell[1]))
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    sheet.save(path)
    print(f"sheet: {path} ({sheet.size[0]}x{sheet.size[1]})")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rig-dir", default=GAPS.RIG_DIR)
    parser.add_argument("--readability", action="store_true")
    parser.add_argument("--timing", action="store_true")
    parser.add_argument("--budget", action="store_true")
    parser.add_argument("--gaps", action="store_true")
    parser.add_argument("--rigid", action="store_true")
    parser.add_argument("--sweep-wide", action="store_true")
    parser.add_argument("--steps", type=int, default=12)
    parser.add_argument("--sheet")
    parser.add_argument("--check-chain", action="store_true")
    parser.add_argument(
        "--strike-chain",
        default=os.path.join(
            os.path.dirname(HERE), "src", "DungeonFortress.Presentation",
            "StrikeChain.cs"))
    parser.add_argument(
        "--blow-effects",
        default=os.path.join(
            os.path.dirname(HERE), "src", "DungeonFortress.Presentation",
            "BlowEffects.cs"))
    parser.add_argument("--frame-sheet")
    parser.add_argument("--frames", nargs="*", default=[])
    parser.add_argument("--working-zoom", action="store_true")
    parser.add_argument("--json")
    arguments = parser.parse_args()

    if arguments.check_chain and not check_chain(
            arguments.strike_chain, arguments.blow_effects):
        raise SystemExit(
            "evidence/263-chain-shipped.json is not the chain the runtime plays.")

    rig = GAPS.load_rig(arguments.rig_dir)
    scale = source_to_world(rig)
    report = {
        "issue": 263,
        "chain": "evidence/263-chain-shipped.json",
        "interpolation": INTERPOLATION,
        "rig": os.path.join(arguments.rig_dir, GAPS.RIG_FILE).replace("\\", "/"),
        "workingZoom": WORKING_ZOOM,
        "tileSize": TILE_SIZE,
        "sourceToScreenScale": round(scale, 6),
        "bodyHeightScreenPx": round(
            (rig["source_body_bbox"][3] - rig["source_body_bbox"][1]) * scale, 3),
        "millisecondsPerTick": round(MILLISECONDS_PER_TICK, 3),
        "displayFrameMilliseconds": round(FRAME_MILLISECONDS, 3),
        "stanceAreaPx": {
            group: area(
                to_screen(
                    drawn_body(rig, arguments.rig_dir, parts, {}, {}, 0.0, 0.0, 1.0),
                    scale))
            for group, parts in GROUPS.items()
        },
    }
    print(
        f"working zoom {WORKING_ZOOM}: the body is "
        f"{report['bodyHeightScreenPx']} screen px tall, "
        f"{report['stanceAreaPx']['whole']} px of silhouette")

    if arguments.readability:
        report["readability"] = readability(rig, arguments.rig_dir, arguments.steps)
        for row in report["readability"]:
            print(
                f"{row['chain']}.{row['duelFrame']:2d} alpha={row['alpha']:.3f}  "
                f"whole={row['wholeChangedPx']:5d}  body={row['bodyChangedPx']:5d}  "
                f"arm={row['armChangedPx']:5d}  bodyPose={row['bodyPosePx']:4d}  "
                f"lean={row['leanDegrees']:+6.2f}")
        for chain in ("strike", "flinch"):
            rows = [row for row in report["readability"] if row["chain"] == chain]
            peak = max(rows, key=lambda row: row["wholeChangedPx"])
            report["peak" + chain.capitalize()] = {
                "duelFrame": peak["duelFrame"],
                "alpha": peak["alpha"],
                "wholeChangedPx": peak["wholeChangedPx"],
                "bodyChangedPx": peak["bodyChangedPx"],
                "armChangedPx": peak["armChangedPx"],
                "bodyPosePx": peak["bodyPosePx"],
                "worstBodyPosePx": max(row["bodyPosePx"] for row in rows),
            }
            print(
                f"peak of {chain}: frame {peak['duelFrame']}, whole "
                f"{peak['wholeChangedPx']} px, body {peak['bodyChangedPx']} px, "
                f"arm {peak['armChangedPx']} px, shape of the body alone "
                f"{max(row['bodyPosePx'] for row in rows)} px at its widest")

    if arguments.timing:
        report["timing"] = timing(rig, arguments.rig_dir)

    if arguments.budget:
        report["budget"] = budget()

    if arguments.gaps:
        report["chainGaps"] = gaps_along_chain(rig, arguments.rig_dir)
        worst = max(report["chainGaps"], key=lambda row: row["slitPixels"])
        report["worstSlitPixels"] = worst["slitPixels"]
        report["worstSlitAt"] = {"chain": worst["chain"], "alpha": worst["alpha"]}
        print(
            f"worst slit on the sampled chain: {worst['slitPixels']} px^2 "
            f"at {worst['chain']} alpha {worst['alpha']}")

    if arguments.rigid:
        report["rigidRotation"] = rigid_rotation(rig, arguments.rig_dir)

    if arguments.sweep_wide:
        report["sweepWide"] = sweep_wide(
            rig, arguments.rig_dir, (-60, -50, -45, -40, -35, 35, 40, 45, 50, 60))

    if arguments.frame_sheet:
        frame_sheet(arguments.frames, arguments.frame_sheet, arguments.working_zoom)

    if arguments.sheet:
        contact_sheet(rig, arguments.rig_dir, arguments.sheet, arguments.steps)

    if arguments.json:
        with open(arguments.json, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(report, handle, indent=2, ensure_ascii=False)
            handle.write("\n")
        print(f"json: {arguments.json}")


if __name__ == "__main__":
    main()
