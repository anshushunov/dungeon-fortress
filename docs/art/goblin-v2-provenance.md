# Goblin v2 — 170% visual-scale generated art

Status: generated art for the 170% creature-scale pass selected by the owner on
2026-08-01 after spike #142.

Runtime candidates live in
`src/DungeonFortress.Game/assets/generated/goblins/`:
`goblin_idle_v2.png`, `goblin_work_v2.png`, `goblin_combat_v2.png`,
`goblin_downed_v2.png`, `goblin_windup_v2.png`, and
`goblin_flinch_v2.png`. They are transparent 272×192 PNGs with a 17:12 canvas
prepared for a **61.8 px canvas height** — that is the draw size
`CameraView.GoblinDrawSize` produces at tile 40 under the owner-selected 170 %,
and it is the height of the *canvas*, not of the body: the body fills 168 of the
192 rows, so it renders at about 54.1 px. See «Integration boundary» below for the
87.55 px width that follows. Crew and raiders share the same
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
  `exec-993a3e19-1b8c-4c8c-93ef-244fb9a3d9d9.png`. SHA-256
  `5173884b71b16c59ab08567fb5ddbefc6997f0de30fc67c1c6fa27093c996b0a`; lives on
  the generating machine at
  `~/.codex/generated_images/019fbdfd-2477-7ff0-ac5a-259b2e984de0/`; the large
  source and intermediate alpha sheet remain outside Git per the decision in
  [`PROVENANCE_VERIFIABILITY.md`](PROVENANCE_VERIFIABILITY.md) (Issue #179)
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

**Запись прогона 2026-08-01 (Issue #163). Это протокол того, что было сделано
тогда, а не описание файлов в их сегодняшнем состоянии.** Поза `flinch`
перегенерирована 2026-08-02 в рамках [Issue #165](https://github.com/anshushunov/dungeon-fortress/issues/165),
и её действующие размеры, размещение и альфа-границы — в разделе
[«Issue #165 — flinch with spear regeneration»](#issue-165--flinch-with-spear-regeneration)
ниже. Числа этого раздела для `flinch` устарели и оставлены на месте намеренно:
свидетельство на момент принятия не подновляется.

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
   188×149, windup 186×158, ~~flinch 182×164~~, and downed 188×84. Body height
   and state scale are therefore unchanged.
   `flinch` — **185×164 с 2026-08-02**, Issue #165, см. раздел ниже.
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
   | ~~`flinch`~~ | ~~29,24~~ | ~~135.5~~ | ~~29,24–211,188~~ |
   | `flinch` **с 2026-08-02** | 26,24 | 135.5 | 26,24–211,188 |
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

## Issue #165 — flinch with spear regeneration

The original `goblin_flinch_v2.png` met the pack-level consistency criteria,
but omitted the short spear held in `combat` and `windup`. For the declared
hit-feedback consumer in #77, the weapon's disappearance and return would read
as item blinking. On 2026-08-02 only `flinch` was regenerated so the spear
remains visibly held throughout `combat → flinch → combat`.

### Reproduction record

- Date: 2026-08-02
- Tool: Codex built-in OpenAI image generation (`imagegen` skill, built-in
  edit mode)
- Model: the built-in tool did not expose an API model identifier; no model
  name is inferred here
- Inputs: `goblin_flinch_v2.png` as the edit target, with
  `goblin_combat_v2.png` and `goblin_windup_v2.png` as character and weapon
  references
- Generation parameters exposed by the tool: exact prompt below, one output;
  seed, quality, input fidelity, and requested pixel size were not exposed
- Generated source: one 1408×1117 PNG, default built-in output
  `exec-d14b69b4-78bb-408e-bd7e-89524e292621.png`. SHA-256
  `11c09668eca933fa5c1795510a46425781ff5e895bdbc6eba2d0c5bc41781dea`; lives on
  the generating machine at
  `~/.codex/generated_images/019fc1a1-d585-7732-b36c-a1fa9dcd1390/`; the large
  chroma-key source and intermediate alpha image remain outside Git per the
  decision in [`PROVENANCE_VERIFIABILITY.md`](PROVENANCE_VERIFIABILITY.md)
  (Issue #179)
- Requested chroma key: `#ff00ff`; the border sampler measured the generated
  background as `#fb03f9`
- Manual paint/editing: none

Exact prompt:

> Use case: precise-object-edit
> Asset type: 2D top-down three-quarter game character sprite for Dungeon Fortress
> Input images: Image 1: edit target flinch pose; Image 2: combat pose weapon and character reference; Image 3: windup pose weapon and character reference
> Primary request: Change only Image 1 by adding the same short spear from Images 2 and 3 into the recoiling goblin's hands. The spear must remain visibly held during the hit reaction and read as continuous motion from windup/combat, not as a different weapon. Preserve the strong backward flinch, facial expression, body pose, character identity, handedness, outfit, palette, chunky pixel-art-inspired rendering, scale, and three-quarter facing direction of Image 1.
> Scene/backdrop: perfectly flat solid #ff00ff chroma-key background for local background removal, uniform edge to edge, with no shadow, gradient, texture, floor, reflection, or lighting variation.
> Composition/framing: one isolated full-body goblin with the entire short spear fully visible and generous padding; keep the feet/support point and visual body scale compatible with all six v2 poses. Arrange the spear diagonally across or just in front of the recoiling body as a plausible continuation of the combat/windup weapon motion; it must still be gripped, not flying loose.
> Color palette: preserve the olive/yellow green skin, dark teal cloth, dark brown leather/boots, warm orange accent, charcoal outline; match the spear's brown shaft, orange binding, and pale metal leaf-shaped point from Images 2 and 3; never use magenta on the subject.
> Constraints: change only the missing weapon/hand interaction; exactly one goblin and one short spear; no redesign, no new props, no extra limbs, no cropped ears or weapon, no cast/contact shadow, no text, labels, logos, watermark, frame, or scenery.

### Post-processing and verification

1. Removed the generated background with the installed
   `remove_chroma_key.py` helper using `--auto-key border --soft-matte
   --transparent-threshold 12 --opaque-threshold 220 --despill`. The helper
   reported 1,196,694 fully transparent and 6,883 partially transparent pixels
   out of 1,572,736.
2. Cropped to the non-zero alpha bounds `181,137–1149,995`, checked all
   8-connected components on the full `alpha > 0` mask, and retained the
   largest. The source had one component, so zero alpha pixels were removed.
3. Resized the cropped sprite from 968×858 to 185×164 with Pillow LANCZOS,
   preserving the previous flinch height of 164 px and nearly the same width
   (previously 182 px).
4. Placed it at `26,24` in a fresh transparent 272×192 RGBA canvas. Using the
   pack's existing `alpha > 32` support-zone method over `172 <= y <= 187`, the
   final support center is `x = 135.5`. The final alpha bbox is
   `26,24–211,188`, the last non-transparent row is `y = 187`, and both outer
   canvas columns remain transparent.
5. Saved an optimized PNG. No paint-over, palette replacement, or other manual
   correction was applied.
6. Reviewed all six poses side by side at the runtime-equivalent 62 px canvas
   height. The goblin identity, palette, scale, baseline, and facing direction
   remain consistent; the short spear is readable in `combat`, `windup`, and
   `flinch`, and the `combat → flinch → combat` transition no longer blinks the
   weapon.

### Reproducible verification commands

Все измерения получены одноразовыми inline Python/Pillow-скриптами, которых нет
в репозитории. Команды запускаются из корня worktree ветки
`agent/165-flinch-with-spear`.

#### 1. RGBA 272×192 у всех шести поз

```powershell
@'
from PIL import Image
from pathlib import Path

base = Path("src/DungeonFortress.Game/assets/generated/goblins")
for state in ["idle", "work", "combat", "windup", "flinch", "downed"]:
    image = Image.open(base / f"goblin_{state}_v2.png")
    print(f"{state}: mode={image.mode}, size={image.width}x{image.height}")
'@ | python -
```

Ожидаемый результат:

```text
idle: mode=RGBA, size=272x192
work: mode=RGBA, size=272x192
combat: mode=RGBA, size=272x192
windup: mode=RGBA, size=272x192
flinch: mode=RGBA, size=272x192
downed: mode=RGBA, size=272x192
```

#### 2. Последняя непрозрачная строка `y=187`

```powershell
@'
from PIL import Image
from pathlib import Path

base = Path("src/DungeonFortress.Game/assets/generated/goblins")
for state in ["idle", "work", "combat", "windup", "flinch", "downed"]:
    alpha = Image.open(base / f"goblin_{state}_v2.png").getchannel("A")
    print(f"{state}: last_nontransparent_row={alpha.getbbox()[3] - 1}")
'@ | python -
```

Все шесть строк должны содержать `last_nontransparent_row=187`.

#### 3. Alpha bbox flinch `26,24–211,188`

Координаты Pillow имеют вид `[left, top, right, bottom)`, то есть правая и
нижняя границы исключающие.

```powershell
@'
from PIL import Image

alpha = Image.open(
    "src/DungeonFortress.Game/assets/generated/goblins/goblin_flinch_v2.png"
).getchannel("A")
print(alpha.getbbox())
'@ | python -
```

Ожидаемый результат:

```text
(26, 24, 211, 188)
```

#### 4. Центр опоры `135.5`

Используется метод provenance: `alpha > 32`, support zone
`172 <= y <= 187`.

```powershell
@'
from PIL import Image

image = Image.open(
    "src/DungeonFortress.Game/assets/generated/goblins/goblin_flinch_v2.png"
).convert("RGBA")
alpha = image.getchannel("A")
pixels = alpha.load()

xs = [
    x
    for y in range(172, 188)
    for x in range(image.width)
    if pixels[x, y] > 32
]

print((min(xs) + max(xs)) / 2)
'@ | python -
```

Ожидаемый результат:

```text
135.5
```

#### 5. Прозрачность всех краёв холста

```powershell
@'
from PIL import Image
from pathlib import Path

base = Path("src/DungeonFortress.Game/assets/generated/goblins")
for state in ["idle", "work", "combat", "windup", "flinch", "downed"]:
    image = Image.open(base / f"goblin_{state}_v2.png").convert("RGBA")
    alpha = image.getchannel("A")
    pixels = alpha.load()
    width, height = image.size

    edges = {
        "top": all(pixels[x, 0] == 0 for x in range(width)),
        "bottom": all(pixels[x, height - 1] == 0 for x in range(width)),
        "left": all(pixels[0, y] == 0 for y in range(height)),
        "right": all(pixels[width - 1, y] == 0 for y in range(height)),
    }
    print(f"{state}: {edges}")
'@ | python -
```

Для каждой позы ожидаются `True` у `top`, `bottom`, `left` и `right`.

#### 6. Просмотр шести поз при высоте 62 px

Это был **ручной просмотр**, не автоматическое измерение. Контактный лист был
создан одноразовым inline Python/Pillow-скриптом, которого нет в репозитории, и
просмотрен через Codex `view_image` в режиме `detail=original`.

```powershell
@'
from PIL import Image
from pathlib import Path

base = Path("src/DungeonFortress.Game/assets/generated/goblins")
states = ["idle", "work", "combat", "windup", "flinch", "downed"]
thumb_size = (88, 62)
gap = 8

sheet = Image.new(
    "RGBA",
    (len(states) * thumb_size[0] + (len(states) - 1) * gap, thumb_size[1]),
    (48, 44, 54, 255),
)

for index, state in enumerate(states):
    image = Image.open(base / f"goblin_{state}_v2.png").convert("RGBA")
    image = image.resize(thumb_size, Image.Resampling.LANCZOS)
    sheet.alpha_composite(image, (index * (thumb_size[0] + gap), 0))

output = Path("issue165-six-poses-62px.png")
sheet.save(output, optimize=True)
print(output.resolve())
'@ | python -
```

Формулировка для PR:

> Ручной просмотр шести поз рядом при высоте холста 62 px: проверены сохранение
> персонажа, палитры, масштаба, базовой линии и направления; копьё остаётся
> читаемым в `combat`, `windup` и `flinch`. Контактный лист создан приведённым
> выше одноразовым inline Python/Pillow-скриптом и просмотрен через Codex
> `view_image(detail=original)`. Внутренняя проверка исполнителя; независимый
> блочный review не заявляется.

## Verifiability (Issue #179, 2026-08-02)

Пост-обработка и её исполнимость по репозиторию. Числа ниже измерены прогоном
восстановленного скрипта на реальных источниках; команды и хеши — в
[`evidence/179-analysis.json`](../../evidence/179-analysis.json).

1. **Chroma key** — executable. `scripts/art/remove_chroma_key.py` в этом
   репозитории побайтово совпадает с использованным хелпером (SHA-256
   `7e512369...`). Запуск из корня репозитория на источнике пака:

   ```powershell
   python scripts/art/remove_chroma_key.py --input <v2-source.png> --out <alpha.png> `
     --auto-key border --soft-matte --transparent-threshold 12 --opaque-threshold 220 --despill
   ```

   Даёт `Key color: #fb03f9; Transparent pixels: 1254108/1572864; Partially
   transparent pixels: 12954/1572864` — дословно числа раздела «Post-processing»
   выше, и снятая alpha-пластина побайтово совпадает с сохранённой
   промежуточной `sha256 bc845f9a...`. Для источника flinch тот же прогон даёт
   `1196694/1572736` и `6883/1572736` — дословно числа раздела «Issue #165»,
   а bbox альфы `(181, 137, 1149, 995)` совпадает с записанным crop bounds
   `181,137–1149,995`; на маске `alpha > 0` ровно один 8-connected компонент.
2. **Шаги 2–6 (split на 512×512, crop, 8-connected cleanup, LANCZOS resize,
   placement в 272×192)** — not executable from the repo: выполнялись
   одноразовыми inline Python/Pillow-скриптами, которые не сохранились.
   Результат проверяем по закоммиченным финалам и по командам раздела
   «Reproducible verification commands» выше (RGBA 272×192, последняя строка
   `y=187`, bbox flinch `26,24–211,188`, центр опоры `135.5`, прозрачные края).
   Источник происхождения каждого шага привязан к источнику пака или flinch
   выше, с контрольной суммой, — но сам шаг перезапустить нельзя.
