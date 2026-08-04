# Goblin cutout v1: provenance and pivot contract

Status: source-resolution cutout art for the ADR 0020 skeletal-animation trial,
Issue #243. Runtime integration and animation belong to Issue #244.

The deliverables live in
`src/DungeonFortress.Game/assets/generated/goblins/cutout_v1/`. The body has
six independently transformable parts (`head`, `torso`, `arm_near`,
`arm_far`, `leg_near`, `leg_far`) plus a separate `weapon` layer. This is the
smallest set that permits the requested strike chain:

- `arm_near` carries windup, strike, follow-through, and return around the
  shoulder;
- `arm_far` counterbalances and can support a two-handed grip;
- `torso` leans independently from the planted lower body;
- `head` turns toward the target independently from the torso;
- the two legs provide an asymmetric step and crouch;
- `weapon` is separate equipment rather than paint baked into the arm.

No production code and none of the six v2 runtime poses were changed.

## Reproduction record

- Date: 2026-08-04
- Source-generation tool: Codex built-in OpenAI image generation (`imagegen`
  skill, built-in tool mode), inherited unchanged from the v2 pack
- Model: the original built-in call did not expose an API model identifier; no
  model name is inferred
- Cutout tool: Python 3 with Pillow 12.3.0 and the committed builder
  `evidence/243-build-goblin-cutout.py`
- Generation parameters exposed by the original tool: exact prompt below, one
  output; seed, quality, and requested pixel size were not exposed
- Generated source: 1536×1024 PNG
  `exec-993a3e19-1b8c-4c8c-93ef-244fb9a3d9d9.png`, SHA-256
  `5173884b71b16c59ab08567fb5ddbefc6997f0de30fc67c1c6fa27093c996b0a`;
  it remains outside Git in the original built-in output directory documented
  by [`goblin-v2-provenance.md`](goblin-v2-provenance.md)
- Alpha source after the repository chroma-key helper: 1536×1024 PNG,
  SHA-256
  `bc845f9afa3759819b7ec0943d737761583e918ea59609883a141e9b654a1bd9`;
  it remains in `.artifacts/243/` and outside Git
- Requested chroma key: `#ff00ff`; the helper measured `#fb03f9`
- New image-generation calls for Issue #243: none. The preferred cut-existing-
  sprite path from `ANIMATION_PIPELINE.md` was available, so regenerating the
  character would only introduce identity and palette drift.

Exact original prompt:

> Use case: stylized-concept
> Asset type: 2D top-down three-quarter game character sprite sheet for Dungeon Fortress
> Primary request: Create one consistent small green dungeon goblin in an exact 3 columns by 2 rows sprite sheet with six distinct states, ordered left-to-right and top-to-bottom: idle standing; work actively swinging a compact pick or hammer; combat in a ready stance holding a short spear; windup visibly drawing the short spear back before a strike; flinch visibly recoiling backward after being hit; downed lying on the ground.
> Scene/backdrop: perfectly flat solid #ff00ff chroma-key background for local background removal. Every cell must have the same uniform #ff00ff background with no panel borders.
> Subject: exactly the same goblin in all six cells: olive-green skin, oversized pointed ears, bald round head, strong brow, small tusks, dark teal sleeveless tunic/shoulder cloth, dark brown belt and trousers, warm orange belt accent, brown boots and wraps. Preserve the same face, proportions, outfit, palette, handedness, and three-quarter facing direction across every pose.
> Style/medium: pixel-art-inspired polished 2D game sprite; chunky hard-edged silhouette; limited palette; controlled highlights and dark outline; preserve the visual language of the existing Dungeon Fortress goblin v1 while adding detail suitable for display at 61.8 pixels tall.
> Composition/framing: exact regular 3x2 grid, one isolated full-body pose centered in each equal square cell, generous and equal padding, no overlap between cells. Upright bodies occupy about 87.5% of cell height. Feet share the same baseline in all upright poses. The downed pose is a clear horizontal silhouette resting on that same baseline. Keep weapons fully inside their cell.
> Lighting/mood: neutral readable game-sprite lighting, identical across all cells.
> Color palette: olive/yellow green skin, dark teal cloth, dark brown leather and boots, small warm orange accent, charcoal outline; never use magenta on the subject.
> Constraints: exactly six goblins and exactly one pose per cell; top-down three-quarter view on an orthogonal grid; identical character design and scale; actions must read clearly at small size; perfectly flat solid #ff00ff background only; crisp separated edges; no cast shadow, contact shadow, floor, scenery, text, labels, logos, watermark, frame, grid lines, extra props, extra characters, duplicate limbs, cropped ears, or cropped weapons.

## Post-processing and manual work

1. The source background was removed with the committed
   `scripts/art/remove_chroma_key.py` using `--auto-key border --soft-matte
   --transparent-threshold 12 --opaque-threshold 220 --despill`. It reproduced
   the v2 provenance measurement: 1,254,108 transparent and 12,954 partially
   transparent pixels out of 1,572,864.
2. The 1536×1024 alpha sheet was split into 512×512 cells. Body material comes
   from the top-left `idle` cell; the separate spear comes from the top-right
   `combat` cell. Parts remain at source resolution. The runtime target remains
   116×168 inside the unchanged 272×192/170% presentation boundary.
3. Authored polygons partition the idle cell by semantic part. Limb masks use
   anatomical ownership boundaries rather than global dilation: the belt,
   buckle, skirt, and scarf remain on `torso` instead of rotating with a limb.
   Fully opaque source pixels are duplicated in a 14 px radius around each
   pivot so small rotations have real overlap instead of seams.
4. Occluded material that does not exist in the flattened source is manually
   reconstructed only where a higher rest-pose layer is fully opaque: teal
   a rounded cloth cap behind the near shoulder and hip, a skin neck under the
   head, and a rounded far-shoulder cap under the torso. Colors are the measured v2 palette:
   skin `(144,144,48)` with `(194,185,75)` highlight, dark teal cloth, and the
   existing charcoal outline. These pixels are hidden in idle and become
   visible only when a joint rotates.
5. The combat-cell spear is separated by geometry and palette. The two
   hand-occluded shaft intervals are bridged manually with colors sampled from
   the visible v2 shaft; the visible wood, binding, outline, and blade pixels
   are then pasted back from the source. No new weapon design was introduced.
6. Each part is cropped with eight transparent pixels of padding. The builder
   emits the PNGs, the JSON contract, the contact sheet, a non-zero-angle joint
   check sheet, and a SHA-256 manifest.
   No paint-over or palette replacement beyond the explicitly named occlusion
   fills and shaft bridge was applied.

## Pivot JSON contract

`goblin_cutout_rig_v1.json` is the single machine-readable source of pivot and
layer data. Coordinates use source-cell pixels with `(0,0)` at the top-left.
For each part:

- `file` is relative to the JSON file;
- `pivot` is `[x,y]` inside that part's cropped PNG;
- `rest_position` is the part PNG's top-left position in the 512×512 source
  cell;
- `parent` names the parent part, or is `null` for root `torso`;
- `z_index` is ascending back-to-front draw order;
- `motion` explains why the part exists.

The metadata also records `source_body_bbox`, `runtime_target_size`, and the
fact that `weapon` is intentionally hidden in the idle rest pose. Issue #244
may convert these source-space values to runtime scale; it must not retype or
replace the pivots.

## Reproduction and verification

Run from the repository root. The external source path is the one recorded in
[`goblin-v2-provenance.md`](goblin-v2-provenance.md); it is intentionally not
committed.

```powershell
New-Item -ItemType Directory -Force .artifacts\243 | Out-Null

python scripts\art\remove_chroma_key.py `
  --input <path-to-exec-993a3e19-1b8c-4c8c-93ef-244fb9a3d9d9.png> `
  --out .artifacts\243\source-alpha.png `
  --auto-key border --soft-matte `
  --transparent-threshold 12 --opaque-threshold 220 --despill

python evidence\243-build-goblin-cutout.py `
  --alpha-sheet .artifacts\243\source-alpha.png `
  --out-dir src\DungeonFortress.Game\assets\generated\goblins\cutout_v1 `
  --contact-sheet evidence\243-goblin-cutout-contact-sheet.png `
  --joint-check evidence\243-goblin-cutout-joint-rotation-check.png `
  --manifest evidence\243-goblin-cutout-manifest.json
```

The builder fails unless the alpha sheet is 1536×1024 and unless compositing
all rest-visible parts reproduces the complete 512×512 idle cell byte-for-byte.
The recorded successful run assigns every visible source pixel to a part (the
manifest records pixels that fall outside the limb polygons and are therefore
assigned to `torso`) and reports
`rest reconstruction: byte-identical RGBA to idle source cell`. Output hashes
are in `evidence/243-goblin-cutout-manifest.json`.

The contact sheet shows, left to right, the source idle cell, the reconstructed
idle body, and the seven separate source-resolution parts including the spear.
It was reviewed at original size: source and reconstruction preserve the same
silhouette, palette, three-quarter facing, proportions, and baseline. This is
the required internal art check; independent PR review remains separate.

`evidence/243-goblin-cutout-joint-rotation-check.png` is the regression image
for the first review finding. It renders `arm_near` at -15°, -10°, and +15°,
`arm_far` at +10°, and `leg_near` at -10° and +10° around the JSON pivots. The
belt, buckle, skirt, and scarf remain on the torso in every panel; the detached
torso-coloured shards and transparent joint holes from the initial cut are no
longer present.

There is no mutant: Issue #243 changes neither simulation, determinism, nor a
runtime contract. Verification is the byte-identical rest reconstruction,
visual contact-sheet review, asset import, and full `scripts/verify.ps1` run.
