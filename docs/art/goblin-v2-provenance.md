# Goblin v2 — 170% visual-scale generated art

Status: generated art for the 170% creature-scale pass selected by the owner on
2026-08-01 after spike #142.

Runtime candidates live in
`src/DungeonFortress.Game/assets/generated/goblins/`:
`goblin_idle_v2.png`, `goblin_work_v2.png`, `goblin_combat_v2.png`,
`goblin_downed_v2.png`, `goblin_windup_v2.png`, and
`goblin_flinch_v2.png`. They are transparent 272×192 PNGs with a 17:12 canvas
prepared for a 61.8 px creature body height. Crew and raiders share the same
generated pack; the runtime distinguishes factions with the existing teal/red
outline, so no second raider set was generated.

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
3. In every source cell, cropped to the original non-zero alpha bounds, found
   all 8-connected components on the full `alpha > 0` mask, and retained only
   the largest component before resize. The same cleanup is applied to all six
   states, rather than being a `work`-only exception:

   | State | Source components | Removed components | Removed alpha pixels |
   |---|---:|---:|---:|
   | `idle` | 1 | 0 | 0 |
   | `work` | 7 | 6 | 1,841 |
   | `combat` | 1 | 0 | 0 |
   | `windup` | 1 | 0 | 0 |
   | `flinch` | 1 | 0 | 0 |
   | `downed` | 1 | 0 | 0 |

   Thus the generated floor debris and all its antialiased fringe are removed
   from `work`; no intended pixels are removed from the other five poses.
4. Resized the cleaned source cells with Pillow LANCZOS to the same dimensions
   used before the canvas correction: idle 116×168, work 178×151, combat
   188×149, windup 186×158, flinch 182×164, and downed 188×84. Body height and
   state scale are therefore unchanged.
5. Placed each resized state in a fresh transparent 272×192 RGBA canvas
   (aspect ratio 17:12). The support zone is `172 <= y <= 187`; its horizontal
   center is `(min_x + max_x) / 2` over pixels with `alpha > 32`. The target
   is the canvas center `x = 135.5`. Integer placement is rounded to the
   nearest pixel with half values rounded away from zero:

   | State | Placement x,y | Final support center | Final alpha bbox |
   |---|---|---:|---|
   | `idle` | 75,20 | 135.5 | 75,20–191,188 |
   | `work` | 85,37 | 136.0 | 85,37–263,188 |
   | `combat` | 81,39 | 135.5 | 81,39–269,188 |
   | `windup` | 30,30 | 136.0 | 30,30–216,188 |
   | `flinch` | 29,24 | 135.5 | 29,24–211,188 |
   | `downed` | 43,104 | 135.5 | 43,104–231,188 |

   The final support-center spread is 0.5 px. Every state ends on the same last
   non-transparent row `y = 187` (exclusive alpha-bound bottom `188`), and
   columns `x = 0` and `x = 271` are fully transparent. No content is clipped
   or wrapped.
6. Saved optimized PNGs. No paint-over, palette replacement, regeneration, or
   other manual correction was applied.

## Integration boundary

This art task does not change runtime code. `Main.cs` still maps the four
currently supported keys (`idle`, `work`, `combat`, `downed`) to v1 assets.
When #77 connects v2, runtime rendering must preserve the 17:12 canvas with a
rectangle rather than the current square `drawSize × drawSize`: at 61.8 px
height the proportional width is 87.55 px. That task also connects the new
`windup` and `flinch` states.
