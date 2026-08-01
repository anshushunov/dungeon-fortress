# Goblin v2 — 170% visual-scale generated art

Status: generated art for the 170% creature-scale pass selected by the owner on
2026-08-01 after spike #142.

Runtime candidates live in
`src/DungeonFortress.Game/assets/generated/goblins/`:
`goblin_idle_v2.png`, `goblin_work_v2.png`, `goblin_combat_v2.png`,
`goblin_downed_v2.png`, `goblin_windup_v2.png`, and
`goblin_flinch_v2.png`. They are transparent 192×192 PNGs prepared for the
61.8 px creature draw size. Crew and raiders share the same generated pack;
the runtime distinguishes factions with the existing teal/red outline, so no
second raider set was generated.

## Reproduction record

- Date: 2026-08-01
- Tool: Codex built-in OpenAI image generation (`imagegen` skill, built-in
  tool mode)
- Model: the built-in tool did not expose an API model identifier; no model
  name is inferred here
- Generation parameters exposed by the tool: exact prompt below, one output;
  seed, quality, and requested pixel size were not exposed
- Generated source: one 3×2 sheet, 1536×1024 PNG, default built-in output
  `exec-993a3e19-1b8c-4c8c-93ef-244fb9a3d9d9.png`; the large source and
  intermediate alpha sheet remain outside Git
- Requested chroma key: `#ff00ff`; the border sampler measured the generated
  background as `#fb03f9`
- Manual paint/editing: none

Exact prompt:

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

## Post-processing

1. Removed the sheet background with the installed
   `remove_chroma_key.py` helper using `--auto-key border --soft-matte
   --transparent-threshold 12 --opaque-threshold 220 --despill`. The helper
   reported 1,254,108 fully transparent and 12,954 partially transparent
   pixels out of 1,572,864.
2. Split the 1536×1024 alpha sheet into six 512×512 cells in prompt order:
   `idle`, `work`, `combat`, `windup`, `flinch`, `downed`.
3. Cropped each cell to its non-zero alpha bounds and resized with Pillow
   LANCZOS. The common base scale is `168 / 330 = 0.509091`, derived from the
   idle sprite's 330 px source height. Wide poses are capped at 188 px so no
   ear, weapon, or body part is clipped during this initial size-normalization
   step: combat uses `0.489583`, downed uses `0.478372`; the other four
   states use the common base scale.
4. Review correction F2 removes the baked debris from `work` before placement.
   The cleanup finds 8-connected components on the full `alpha > 0` mask and
   retains only the largest component (body plus held tool, 15,500 pixels).
   It removes 764 alpha pixels across 25 detached components, including their
   antialiased fringes. On the review's `alpha > 32` measurement the retained
   body has 14,607 pixels; the meaningful discarded fragments begin with 312,
   85, 76, and 25 pixels. No pixel is painted or regenerated.
5. Review correction F1 aligns the body by its support zone instead of
   centering the full alpha bounds with the weapon. The support zone is
   `172 <= y <= 187`; its horizontal center is
   `(min_x + max_x) / 2` over pixels with `alpha > 32`. The target is the
   192 px canvas center `x = 95.5`. Translation uses the nearest integer with
   half values rounded away from zero:

   | State | Original support center | After cleanup | Applied `dx` | Final support center | Final alpha bbox |
   |---|---:|---:|---:|---:|---|
   | `idle` | 98.5 | 98.5 | -3 | 95.5 | 35,20–151,188 |
   | `work` | 93.5 | 58.0 | +38 | 96.0 | 45,37–192,188 |
   | `combat` | 56.5 | 56.5 | +39 | 95.5 | 41,39–192,188 |
   | `windup` | 109.0 | 109.0 | -14 | 95.0 | 0,30–175,188 |
   | `flinch` | 111.5 | 111.5 | -16 | 95.5 | 0,24–171,188 |
   | `downed` | 94.5 | 94.5 | +1 | 95.5 | 3,104–191,188 |

   Translation writes into a fresh transparent 192×192 canvas without vertical
   movement or wraparound. Content outside the fixed canvas is discarded; it
   is not rescaled or repainted. The final non-transparent row remains
   `y = 187` in every state (exclusive alpha-bound bottom `188`).
6. Saved optimized PNGs. No paint-over, palette replacement, regeneration, or
   other manual correction was applied.

## Integration boundary

This art task does not change runtime code. `Main.cs` still maps the four
currently supported keys (`idle`, `work`, `combat`, `downed`) to v1 assets.
An implementation task must switch those paths to v2 and connect the new
`windup` and `flinch` states when hit feedback and procedural animation are
implemented in #77.
