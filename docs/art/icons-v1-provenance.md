# Icons v1 — generated HUD asset pack

Status: prototype-only; this is **not** the final art direction.

Runtime files live in `src/DungeonFortress.Game/assets/icons/`. The pack contains
the sixteen 48×48 RGBA PNGs from
[Issue #54](https://github.com/anshushunov/dungeon-fortress/issues/54). Godot
draws them at exactly 24×24.

## Reproduction record

- Date: 2026-07-28
- Tool: built-in OpenAI image generation
- Model: the built-in tool did not expose a model identifier in this session
- Generation parameters: one 4×4 source sheet; no input/reference images and no
  user-selectable size, quality, seed, or transparency parameters were exposed;
  the returned RGB source was 1254×1254
- Source: `call_4mB0OlBELzdJ6BKBewCnvFu4.png`, retained outside Git with the
  large source and intermediate alpha/preview files
- Chroma key: sampled `#f703f6`, processed by `remove_chroma_key.py` with
  auto-key border, soft matte, thresholds `12/220`, and despill
- Post-process: the sheet was split into its 4×4 cells; each selected cell was
  cropped to non-transparent bounds, reduced with LANCZOS to fit a 20×20 box on
  the actual 24×24 target canvas, mapped to the fixed five-color palette below,
  and hard-matted at alpha `96`; the checked 24×24 result was then enlarged
  with nearest-neighbour to the runtime 48×48 PNG
- Action/cancel pairs: the generated cancel cells were not used. Instead,
  `icon_dig_cancel.png` and `icon_build_cancel.png` were copied pixel-for-pixel
  from their action icon at 24×24, then received the same diagonal cancel mark:
  a 5 px `#07111c` line under a 3 px `#f97360` line from `(3,3)` to `(20,20)`.
  The pair therefore differs only by the slash.
- Fixed palette: outline `#07111c`, main `#dbeafe`, secondary `#5f9ca8`,
  accent `#f59e0b`, cancel `#f97360`
- Manual paint/editing: no pixel-by-pixel painting. Manual work was limited to
  choosing the generated sheet, defining the deterministic crop/scale/palette
  rules, centring every icon on the same canvas, and defining the shared cancel
  slash. All source sheets, scripts, contact sheets, and previews remain outside
  Git.

Exact prompt:

```text
Use case: stylized-concept
Asset type: 4x4 source sheet for 2D dungeon-management HUD icons; final runtime icons are 48x48 and displayed at exactly 24x24
Primary request: Create sixteen chunky hard-edged UI icon concepts in an exact evenly spaced 4x4 grid, row-major order: magnifying glass inspecting a square tile; broad paint brush; block eraser; mining pickaxe; same mining pickaxe crossed by one bold diagonal cancel slash; stockpile as a low crate holding three large material blocks; building blueprint as a simple rolled-corner plan with a bold small wall/hammer shape but absolutely no writing; same building blueprint crossed by one bold diagonal cancel slash; zone selector as one bold dashed square boundary with a solid center diamond; work priority as three thick ascending chevrons; dungeon rule as a sturdy shield with one large keyhole-like circle-and-stem symbol, not a letter; play as one solid right-pointing triangle; pause as two solid vertical bars; step as one solid right-pointing triangle meeting a thick vertical stop bar; cooked food as one large drumstick silhouette; stone as one angular rock chunk.
Scene/backdrop: perfectly flat solid #ff00ff chroma-key background, one uniform color, no shadows, gradients, texture, reflections, floor plane, glow, border, cell panels, or lighting variation
Style/medium: pixel-art-inspired, chunky hard-edged flat raster icon silhouettes, limited palette, no anti-aliased tiny details
Composition/framing: exact 4x4 grid; each icon centered in an equal square cell; identical optical weight; identical generous padding; every silhouette must remain recognizable after reduction to 24x24; no icon touches another cell
Color palette: pale ice blue #dbeafe main shapes, muted teal #5f9ca8 secondary planes, warm orange #f59e0b accents, very dark navy #07111c outline; cancel slashes warm coral #f97360; do not use magenta in any icon
Constraints: no text, no digits, no letters, no labels, no watermark, no baked backgrounds, no individual button tiles; simple opaque shapes only; preserve exactly the same base silhouette in each action/cancel pair and differ only by the diagonal slash; use at most four flat subject colors; thick strokes and large negative spaces; readability by silhouette at 24 pixels is more important than detail
Avoid: fine texture, thin lines, perspective depth, realistic lighting, bevels, gradients, shadows, decorative filigree, tiny marks, complex internal detail, typography, logos
```
