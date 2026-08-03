# План реализации: контур по силуэту вместо кольца стороны

> **Исполнителю:** реализуй задачами по порядку. Шаги помечены чекбоксами
> (`- [ ]`). Каждая задача заканчивается проверяемым результатом и коммитом.

**Цель.** Заменить кольцо стороны радиусом 27 опорных px на контур по силуэту
тела, окрашенный по отношению существа к игроку.

**Архитектура.** Таблица «отношение → цвет + ширина» живёт в сборке
`DungeonFortress.Presentation` и проверяется без движка. Адаптер `Main.cs`
отображает снапшот в отношение и рисует контур восемью смещёнными копиями белого
силуэта позы перед спрайтом; силуэты строятся один раз при загрузке пака из альфы
самого спрайта, поэтому любой будущий пак рас получает индикацию без ручной
разметки.

**Стек.** C# / .NET, Godot 4.7.1 mono, xUnit, PowerShell (`scripts/verify.ps1`).

**Источник.** [`SIDE_INDICATOR.md`](SIDE_INDICATOR.md) — спека, принята владельцем
2026-08-03. Расхождение плана со спекой — дефект плана.

## Общие ограничения

Действуют в каждой задаче.

- Работать **только** в своём worktree, созданном `git worktree add`. Корневую
  рабочую копию не трогать, ветку в ней не переключать
  (`docs/engineering/AGENT_ENTRY.md`).
- Числа и таблица отношений живут в `DungeonFortress.Presentation`, **не** в
  `Main.cs`: `Main.cs` не собирается джобом «Pure .NET»
  ([ADR 0011](../decisions/0011-presentation-layer-without-engine.md)), поэтому
  значение, решённое там, никем не проверяется.
- Цвета: свой `#14b8a6`, чужой `#dc2626`. Ширины: свой `1.2f`, чужой `2.0f`
  опорных пикселя. Смещений восемь.
- **Прозрачность не вводится.** Тихость своего — шириной и цветом. Причина в
  спеке: восемь копий композитятся друг на друга, `1-(1-a)^n`.
- **Член `Neutral` не заводить.** Симуляция не производит нейтралов;
  недостижимая ветка не тестируется.
- Контур рисуется **перед** спрайтом, спрайт поверх.
- Каждое число в теле PR идёт с командой, которой снято (правило 8
  `AGENT_ENTRY.md`).
- Стиль сообщений коммитов — как в истории репозитория; авторство LLM не
  указывать.

## Структура файлов

| Файл | Ответственность |
|---|---|
| `src/DungeonFortress.Presentation/SideOutline.cs` | создаётся: перечисление отношений и таблица «отношение → цвет + ширина», плюс восемь смещений |
| `src/DungeonFortress.Presentation/SideMarker.cs` | удаляется: описывал только кольцо |
| `src/DungeonFortress.Presentation/WorldDrawOrder.cs` | правится: две новые рутины `Draw*` в манифест |
| `src/DungeonFortress.Game/Main.cs` | правится: построение силуэтов, отрисовка контура, снятие колец, текст легенды |
| `tests/DungeonFortress.Presentation.Tests/SideOutlineTests.cs` | создаётся: таблица отношений, чистый тест |
| `tests/DungeonFortress.Presentation.Tests/SideOutlineAdapterTests.cs` | создаётся: структурная проверка адаптера чтением исходника |
| `tests/DungeonFortress.Presentation.Tests/SideMarkerVisibilityTests.cs` | удаляется: держал геометрию кольца |
| `docs/engineering/PROTOTYPE_GRAYBOX.md` | правится: обещание про кольца |

---

### Задача 1: таблица отношений в `Presentation`

Чистая задача, движок не запускается. `SideMarker` пока остаётся на месте и
продолжает использоваться адаптером — константы цвета одну задачу живут в двух
местах, это снимается в задаче 2.

**Файлы:**
- Создать: `src/DungeonFortress.Presentation/SideOutline.cs`
- Создать: `tests/DungeonFortress.Presentation.Tests/SideOutlineTests.cs`

**Интерфейсы:**
- Отдаёт наружу (задача 2 опирается на эти имена):
  `enum BodyRelation { Own, Hostile }`;
  `static string SideOutline.Color(BodyRelation relation)`;
  `static float SideOutline.WidthRef(BodyRelation relation)`;
  `static IReadOnlyList<(float X, float Y)> SideOutline.Offsets`.

- [ ] **Шаг 1: написать падающий тест**

Создать `tests/DungeonFortress.Presentation.Tests/SideOutlineTests.cs`:

```csharp
using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Спека docs/design/SIDE_INDICATOR.md: принадлежность несёт контур по силуэту,
/// окрашенный по отношению существа к игроку. Эти проверки держат таблицу
/// отношений — единственное место, где решено, каким цветом и какой толщиной
/// рисуется сторона. Движок не запускается (ADR 0011).
/// </summary>
public sealed class SideOutlineTests
{
    /// <summary>
    /// Таблица полна: у каждого отношения есть и цвет, и ширина. Добавленный
    /// член перечисления без строки в таблице роняет этот тест, а не игру.
    /// </summary>
    [Fact]
    public void Every_relation_has_a_colour_and_a_width()
    {
        foreach (var relation in Enum.GetValues<BodyRelation>())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(SideOutline.Color(relation)),
                $"Relation {relation} has no outline colour.");
            Assert.True(
                SideOutline.WidthRef(relation) > 0f,
                $"Relation {relation} has no outline width.");
        }
    }

    /// <summary>
    /// Свой и чужой различаются по обоим признакам сразу. Спека требует именно
    /// этого: цвет несёт сторону, ширина несёт громкость, и сведение их к
    /// одному признаку — потеря половины канала.
    /// </summary>
    [Fact]
    public void Own_and_hostile_differ_in_both_colour_and_width()
    {
        Assert.NotEqual(
            SideOutline.Color(BodyRelation.Own),
            SideOutline.Color(BodyRelation.Hostile));
        Assert.NotEqual(
            SideOutline.WidthRef(BodyRelation.Own),
            SideOutline.WidthRef(BodyRelation.Hostile));
    }

    /// <summary>
    /// Громкий канал достаётся редкому событию: чужой в подземелье помечен
    /// сильнее, чем свой. Перестановка ширин местами роняет этот тест.
    /// </summary>
    [Fact]
    public void The_hostile_outline_is_the_louder_of_the_two()
    {
        Assert.True(
            SideOutline.WidthRef(BodyRelation.Hostile) >
            SideOutline.WidthRef(BodyRelation.Own),
            "The hostile outline must be the wider of the two: the loud channel " +
            "belongs to the rare event, not to the standing majority.");
    }

    /// <summary>
    /// Цвета те, что обещает легенда HUD. Свод двух оттенков к одному или их
    /// перестановка между сторонами роняет этот тест.
    /// </summary>
    [Fact]
    public void The_colours_are_the_documented_teal_and_red()
    {
        Assert.Equal("#14b8a6", SideOutline.Color(BodyRelation.Own));
        Assert.Equal("#dc2626", SideOutline.Color(BodyRelation.Hostile));
    }

    /// <summary>
    /// Восемь единичных направлений, все разные. Четырёх мало: угол силуэта
    /// остаётся выщербленным, потому что диагонали не покрыты.
    /// </summary>
    [Fact]
    public void The_outline_is_offset_in_eight_distinct_unit_directions()
    {
        Assert.Equal(8, SideOutline.Offsets.Count);
        Assert.Equal(8, SideOutline.Offsets.Distinct().Count());
        foreach (var (x, y) in SideOutline.Offsets)
        {
            Assert.Equal(1.0, Math.Sqrt((x * x) + (y * y)), 3);
        }
    }
}
```

- [ ] **Шаг 2: убедиться, что тест падает**

```
dotnet test tests/DungeonFortress.Presentation.Tests --filter FullyQualifiedName~SideOutlineTests
```

Ожидание: ошибка компиляции — `BodyRelation` и `SideOutline` не существуют.

- [ ] **Шаг 3: минимальная реализация**

Создать `src/DungeonFortress.Presentation/SideOutline.cs`:

```csharp
namespace DungeonFortress.Presentation;

/// <summary>
/// Что тело значит для игрока. Канал несёт именно отношение, а не то, из какой
/// существо фракции: пак рас общий, поэтому на карте возможен гоблин-свой
/// против гоблина-чужого, и различать стороны нужно независимо от расы. Какая
/// именно фракция — вторичный вопрос, его место в инспекторе и тултипе.
///
/// Нейтрала здесь нет намеренно: симуляция не производит ни одного, а
/// недостижимую ветку нельзя протестировать. Когда нейтралы появятся,
/// добавление члена заставит компилятор перечислить все места, где решение
/// принимается.
/// </summary>
public enum BodyRelation
{
    /// <summary>Существо игрока.</summary>
    Own,

    /// <summary>Враждебное существо.</summary>
    Hostile,
}

/// <summary>
/// Чем обводится тело каждого отношения. Живёт здесь, а не в <c>Main.cs</c>, по
/// причине ADR 0011: адаптер не собирается джобом «Pure .NET», поэтому значение,
/// решённое там, решено там, где его никто не проверяет.
///
/// Заменяет кольцо стороны Issue #177: при клетке 40 px то кольцо имело диаметр
/// 98.2 px против 40 px клетки, то есть занимало около шести клеток площади на
/// одно тело. Контур занимает площадь силуэта.
/// </summary>
public static class SideOutline
{
    /// <summary>Цвет своего, teal. Легенда обещает «teal outline = crew».</summary>
    public const string OwnColor = "#14b8a6";

    /// <summary>Цвет чужого, красный. Легенда обещает «red outline = raider».</summary>
    public const string HostileColor = "#dc2626";

    /// <summary>
    /// Ширина контура своего, в опорных пикселях авторской сетки 22 px. Тише
    /// чужого, но помечен: «не помечен» — сигнал слабее, чем «помечен», и на
    /// третьем отношении такая схема не расширяется.
    /// </summary>
    public const float OwnWidthRef = 1.2f;

    /// <summary>
    /// Ширина контура чужого. 2 опорных px — это 3.6 world px при клетке 40:
    /// достаточно тонко, чтобы не утолщать тело, и достаточно толсто, чтобы
    /// пережить mipmap-уменьшение на обзорном зуме.
    /// </summary>
    public const float HostileWidthRef = 2f;

    /// <summary>
    /// Восемь направлений, в которых смещается копия силуэта. Четырёх мало:
    /// диагонали не покрыты, и угол силуэта остаётся выщербленным.
    /// </summary>
    public static IReadOnlyList<(float X, float Y)> Offsets { get; } =
    [
        (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f),
        (0.70710678f, 0.70710678f), (-0.70710678f, 0.70710678f),
        (0.70710678f, -0.70710678f), (-0.70710678f, -0.70710678f),
    ];

    /// <summary>Цвет контура этого отношения.</summary>
    public static string Color(BodyRelation relation) => relation switch
    {
        BodyRelation.Own => OwnColor,
        BodyRelation.Hostile => HostileColor,
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
    };

    /// <summary>Ширина контура этого отношения, в опорных пикселях.</summary>
    public static float WidthRef(BodyRelation relation) => relation switch
    {
        BodyRelation.Own => OwnWidthRef,
        BodyRelation.Hostile => HostileWidthRef,
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
    };
}
```

- [ ] **Шаг 4: убедиться, что тест зелёный**

```
dotnet test tests/DungeonFortress.Presentation.Tests --filter FullyQualifiedName~SideOutlineTests
```

Ожидание: 5 passed.

- [ ] **Шаг 5: проверить мутант**

Поменять местами тела `BodyRelation.Own` и `BodyRelation.Hostile` в
`SideOutline.Color`, прогнать тест. Ожидание:
`The_colours_are_the_documented_teal_and_red` краснеет. Вернуть как было.

- [ ] **Шаг 6: коммит**

```bash
git add src/DungeonFortress.Presentation/SideOutline.cs tests/DungeonFortress.Presentation.Tests/SideOutlineTests.cs
git commit -m "Таблица отношений тела: цвет и громкость контура вместо кольца"
```

---

### Задача 2: адаптер рисует контур и перестаёт рисовать кольца

Кольцо и контур обязаны меняться одним изменением: сборка, где кольцо снято, а
контур не добавлен, не сообщает игроку сторону вообще.

**Файлы:**
- Изменить: `src/DungeonFortress.Game/Main.cs`
- Изменить: `src/DungeonFortress.Presentation/WorldDrawOrder.cs` (около строки 115)
- Удалить: `src/DungeonFortress.Presentation/SideMarker.cs`
- Удалить: `tests/DungeonFortress.Presentation.Tests/SideMarkerVisibilityTests.cs`
- Создать: `tests/DungeonFortress.Presentation.Tests/SideOutlineAdapterTests.cs`

**Интерфейсы:**
- Использует из задачи 1: `BodyRelation`, `SideOutline.Color`,
  `SideOutline.WidthRef`, `SideOutline.Offsets`.
- Заводит в `Main.cs` (структурный тест ниже опирается на эти имена):
  `private void DrawSidedBody(Vector2 center, string key, BodyRelation relation)`;
  `private void DrawGoblinOutline(Vector2 center, string key, BodyRelation relation)`;
  `private static ImageTexture BuildSilhouette(Image source)`;
  поле `private readonly Dictionary<string, Texture2D> _goblinSilhouettes = [];`.

**Ловушка, которую надо знать заранее.** `WorldDrawPassGuardTests`
(`Every_drawing_routine_of_the_adapter_is_declared`) требует, чтобы **каждый**
метод `Main.cs` с префиксом `Draw` был объявлен в `WorldDrawOrder.All`, был
достижим из `DrawMap` и лежал в том же проходе, что и его вызывающий. Обе новые
рутины объявляются как `WorldDrawPass.Depth`. `BuildSilhouette` под правило не
подпадает — префикса `Draw` у него нет.

Проверка «укрывающих примитивов» новую отрисовку не затрагивает: в
`CoveringPrimitives` перечислены только `DrawRect` и `DrawCircle`, а текстура
там названа штрихом и не спрашивается.

- [ ] **Шаг 1: написать падающий тест**

Удалить `tests/DungeonFortress.Presentation.Tests/SideMarkerVisibilityTests.cs` и
создать `tests/DungeonFortress.Presentation.Tests/SideOutlineAdapterTests.cs`:

```csharp
using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Структурная половина спеки docs/design/SIDE_INDICATOR.md: таблица отношений
/// сама по себе — правило ни о чём, пока адаптер по ней не рисует. Здесь
/// читается исходник Main.cs тем же читателем, что и в
/// <see cref="WorldDrawPassGuardTests"/>: движок не запускается (ADR 0011).
///
/// Заменяет SideMarkerVisibilityTests, который держал геометрию кольца — кольцо
/// снято, и его условие перестало что-либо означать.
/// </summary>
public sealed class SideOutlineAdapterTests
{
    /// <summary>
    /// Оба вида тел рисуются через общую рутину и ни один не рисует кольцо: у
    /// каждого ровно один вызов <c>DrawSidedBody</c>, ни одного
    /// <c>DrawArc</c> и ни одного прямого <c>DrawGoblin</c>.
    /// </summary>
    [Fact]
    public void DrawCreature_and_DrawRaider_draw_the_body_through_the_sided_routine()
    {
        foreach (var routine in new[] { "DrawCreature", "DrawRaider" })
        {
            var body = AdapterSource.Body(routine);
            Assert.Single(AdapterSource.CallsTo(body, "DrawSidedBody"));
            Assert.Empty(AdapterSource.CallsTo(body, "DrawArc"));
            Assert.Empty(AdapterSource.CallsTo(body, "DrawGoblin"));
        }
    }

    /// <summary>
    /// Команда просит своё отношение, рейдер — чужое. Перестановка их местами
    /// красит стороны наоборот, и это ровно то, что тест ловит.
    /// </summary>
    [Fact]
    public void DrawCreature_asks_for_the_own_relation_and_DrawRaider_for_the_hostile_one()
    {
        var crew = AdapterSource.CallsTo(
            AdapterSource.Body("DrawCreature"), "DrawSidedBody")[0];
        Assert.Contains(
            $"{nameof(BodyRelation)}.{nameof(BodyRelation.Own)}",
            crew.Arguments[2],
            StringComparison.Ordinal);

        var raider = AdapterSource.CallsTo(
            AdapterSource.Body("DrawRaider"), "DrawSidedBody")[0];
        Assert.Contains(
            $"{nameof(BodyRelation)}.{nameof(BodyRelation.Hostile)}",
            raider.Arguments[2],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Контур рисуется ПЕРЕД спрайтом: наружу должна выходить только бахрома.
    /// Контур после спрайта — это обводка поверх тела, другая картинка.
    ///
    /// Читатель различает <c>DrawGoblin(</c> и <c>DrawGoblinOutline(</c>: за
    /// именем первого идёт символ, годный в идентификатор, и такой вызов
    /// пропускается.
    /// </summary>
    [Fact]
    public void The_outline_is_drawn_before_the_sprite()
    {
        var body = AdapterSource.Body("DrawSidedBody");

        Assert.Single(AdapterSource.CallsTo(body, "DrawGoblinOutline"));
        Assert.Single(AdapterSource.CallsTo(body, "DrawGoblin"));
        Assert.True(
            body.IndexOf("DrawGoblinOutline(", StringComparison.Ordinal) <
            body.IndexOf("DrawGoblin(", StringComparison.Ordinal),
            "DrawSidedBody draws the outline after the sprite, so it is an " +
            "overlay on the body instead of a fringe around it.");
    }

    /// <summary>
    /// Цвет и ширина контура берутся из таблицы отношений, а не из литерала
    /// рядом. Литерал невидим для любой проверки в репозитории — это тот же
    /// довод, которым живёт проверка альфы в WorldDrawPassGuardTests.
    /// </summary>
    [Fact]
    public void The_outline_takes_its_colour_and_width_from_the_relation_table()
    {
        var body = AdapterSource.Body("DrawGoblinOutline");

        Assert.Contains(
            $"{nameof(SideOutline)}.{nameof(SideOutline.Color)}(",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(SideOutline)}.{nameof(SideOutline.WidthRef)}(",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(SideOutline)}.{nameof(SideOutline.Offsets)}",
            body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Силуэт строится при загрузке пака, а не на каждом кадре, и строится для
    /// каждой позы: контур без силуэта был бы палитрой спрайта, умноженной на
    /// цвет стороны, и унаследовал бы собственные свет и тень гоблина.
    /// </summary>
    [Fact]
    public void Loading_the_pack_builds_a_silhouette_for_every_pose()
    {
        var body = AdapterSource.Body("LoadGoblinSprites");
        Assert.Single(AdapterSource.CallsTo(body, "BuildSilhouette"));
    }

    /// <summary>
    /// Круг под телом снова означает ровно одно — намерение игрока. Кольцо
    /// выделения радиусом 10 опорных px существовало и раньше, но тонуло рядом
    /// с кольцом стороны радиусом 27; после снятия колец единственная дуга в
    /// информационном проходе тела — это выделение. Второй <c>DrawArc</c>,
    /// появившийся здесь, снова нагружает канал двумя значениями.
    /// </summary>
    [Fact]
    public void The_circle_under_a_body_means_selection_and_nothing_else()
    {
        Assert.Single(AdapterSource.CallsTo(
            AdapterSource.Body("DrawCreatureInformation"), "DrawArc"));
        Assert.Empty(AdapterSource.CallsTo(
            AdapterSource.Body("DrawRaiderInformation"), "DrawArc"));
    }
}
```

- [ ] **Шаг 2: убедиться, что тест падает**

```
dotnet test tests/DungeonFortress.Presentation.Tests --filter FullyQualifiedName~SideOutlineAdapterTests
```

Ожидание: падение с `Main.cs declares no method named 'DrawSidedBody'`.

- [ ] **Шаг 3: объявить новые рутины в манифесте**

В `src/DungeonFortress.Presentation/WorldDrawOrder.cs`, в блоке «Pass 2 — the
depth pass itself», после строки `new("DrawGoblin", WorldDrawPass.Depth, null),`
добавить:

```csharp
        new("DrawSidedBody", WorldDrawPass.Depth, null),
        new("DrawGoblinOutline", WorldDrawPass.Depth, null),
```

- [ ] **Шаг 4: реализация в адаптере**

Четыре правки в `src/DungeonFortress.Game/Main.cs`.

**4.1.** Рядом с полем `_goblinSprites` добавить словарь силуэтов:

```csharp
    // Те же шесть поз с каждым непрозрачным пикселем, выкрученным в белый.
    // Копия силуэта под телом должна быть плоской фигурой цвета стороны, а не
    // палитрой спрайта, умноженной на него.
    private readonly Dictionary<string, Texture2D> _goblinSilhouettes = [];
```

**4.2.** В `LoadGoblinSprites`, сразу после `_goblinSprites.Add(state, texture);`:

```csharp
                _goblinSilhouettes.Add(state, BuildSilhouette(imported.GetImage()));
```

и рядом с `LoadGoblinSprites` — сам построитель:

```csharp
    /// <summary>
    /// Та же поза с белым цветом и сохранённой альфой, чтобы отрисовка с
    /// modulate давала плоскую фигуру этого цвета. Байтовый проход, а не
    /// попиксельный: 192x272 на шесть поз — это 313 тысяч пикселей, и делать их
    /// через SetPixel значит платить за загрузку заметную задержку.
    /// </summary>
    private static ImageTexture BuildSilhouette(Image source)
    {
        source.Convert(Image.Format.Rgba8);
        var data = source.GetData();
        for (var index = 0; index + 3 < data.Length; index += 4)
        {
            data[index] = 255;
            data[index + 1] = 255;
            data[index + 2] = 255;
        }

        var silhouette = Image.CreateFromData(
            source.GetWidth(),
            source.GetHeight(),
            false,
            Image.Format.Rgba8,
            data);
        silhouette.GenerateMipmaps();
        return ImageTexture.CreateFromImage(silhouette);
    }
```

**4.3.** В `DrawCreature` заменить вызов спрайта и весь блок `DrawArc` на:

```csharp
        DrawSidedBody(center, CrewSpriteKey(creature), BodyRelation.Own);
```

В `DrawRaider` — заменить вызов спрайта и весь блок `DrawArc` на:

```csharp
        DrawSidedBody(center, RaiderSpriteKey(raider), BodyRelation.Hostile);
```

**4.4.** Рядом с `DrawGoblin` добавить обе новые рутины:

```csharp
    /// <summary>
    /// Тело вместе с тем, что говорит, на чьей оно стороне. Контур идёт перед
    /// спрайтом, спрайт поверх — наружу выходит только бахрома.
    /// </summary>
    private void DrawSidedBody(Vector2 center, string key, BodyRelation relation)
    {
        DrawGoblinOutline(center, key, relation);
        DrawGoblin(center, key);
    }

    /// <summary>
    /// Восемь смещённых копий белого силуэта позы в цвете отношения. Смещения и
    /// ширина — SideOutline'а: адаптер не решает, как выглядит сторона, потому
    /// что решение, принятое здесь, принято там, где его не проверяет джоб
    /// «Pure .NET» (ADR 0011).
    /// </summary>
    private void DrawGoblinOutline(Vector2 center, string key, BodyRelation relation)
    {
        if (!_goblinSilhouettes.TryGetValue(key, out var silhouette))
        {
            return;
        }

        var rect = ToRect2(CameraView.GoblinDrawRect(
            new ViewPoint(center.X, center.Y),
            _tileSize));
        var color = new Color(SideOutline.Color(relation));
        var width = ScaleWorld(SideOutline.WidthRef(relation));
        foreach (var (x, y) in SideOutline.Offsets)
        {
            DrawTextureRect(
                silhouette,
                new Rect2(rect.Position + new Vector2(x * width, y * width), rect.Size),
                false,
                color);
        }
    }
```

- [ ] **Шаг 5: удалить отслуживший тип**

```bash
git rm src/DungeonFortress.Presentation/SideMarker.cs
```

Проверить, что ссылок не осталось:

```
rg -n "SideMarker" src tests
```

Ожидание: пусто.

- [ ] **Шаг 6: убедиться, что тесты зелёные**

```
dotnet test tests/DungeonFortress.Presentation.Tests --configuration Release
```

Ожидание: весь проект зелёный, включая `WorldDrawPassGuardTests` — манифест
пополнен на шаге 3.

- [ ] **Шаг 7: проверить мутант**

Удалить строку `DrawGoblinOutline(center, key, relation);` из `DrawSidedBody`,
прогнать `SideOutlineAdapterTests`. Ожидание:
`The_outline_is_drawn_before_the_sprite` краснеет. Вернуть строку.

- [ ] **Шаг 8: увидеть кадр**

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-game.ps1 -Fixture prepared -ScreenshotTicks 1670 -ScreenshotPath "side/after.png" -TileSize 40 -CameraZoom 2.0 -CameraPosition "900,500" -UiScale 1.0 -FrameSize "1280x720"
```

Ожидание: событие `godot_graybox_screenshot` со `status":"ok"`, на кадре у тел
команды тонкий бирюзовый контур, у рейдеров толстый красный, колец нет.

- [ ] **Шаг 9: коммит**

```bash
git add -A
git commit -m "Сторона тела читается контуром по силуэту, кольцо снято"
```

---

### Задача 3: легенда и документация перестают обещать кольцо

Отделена от задачи 2 намеренно: review может принять отрисовку и отклонить
формулировку.

**Файлы:**
- Изменить: `src/DungeonFortress.Game/Main.cs` (строка легенды HUD ~2046 и
  `RaidLegend()` ~5380)
- Изменить: `docs/engineering/PROTOTYPE_GRAYBOX.md` (~1679)

**Риск, который надо измерить, а не предположить.** Слово `outline` длиннее
`ring` на три символа, а строк со словом две в одной подписи легенды HUD. Ширина
подписи 367 логических px при кегле 8. Если строка перестанет помещаться в одну
линию, это поймает `labelFit` в стадии `ui`. Если поймает — использовать
`edge` вместо `outline` (короче, чем `ring`, на один символ) и сказать об этом в
теле PR.

- [ ] **Шаг 1: строка легенды HUD**

В `src/DungeonFortress.Game/Main.cs` заменить

```csharp
                      ("teal ring = crew / red ring = raider / bar = HP / white X = downed", 8, "#cbd5e1"),
```

на

```csharp
                      ("teal outline = crew / red outline = raider / bar = HP / white X = downed", 8, "#cbd5e1"),
```

- [ ] **Шаг 2: легенда боя**

В `RaidLegend()` заменить строку

```csharp
        "teal ring = crew  •  red ring = raider\n" +
```

на

```csharp
        "teal outline = crew  •  red outline = raider\n" +
```

- [ ] **Шаг 3: документация прототипа**

В `docs/engineering/PROTOTYPE_GRAYBOX.md` заменить абзац, начинающийся
«During a raid, teal ring outlines are crew…», на:

```
During a raid, a teal outline around a body is crew and a red one is a raider;
the raider outline is the wider of the two, so the rarer thing is the louder
mark. The outline is derived from the sprite's own alpha, which is what keeps it
working when a race pack changes (docs/design/SIDE_INDICATOR.md). It replaced
the stroke rings of Issue #177: those were visible, but at 27 reference pixels a
ring covered about six cells of the 40 px grid, and nine bodies in a cluster
turned into overlapping arcs. HP bars appear
```

(остаток абзаца — «under both. Crew dots show working…» — не трогать).

- [ ] **Шаг 4: прогнать стадию, которая ловит переполнение подписи**

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 -Stage ui
```

Ожидание: стадия зелёная. Если `labelFit` сообщает `neededLines` больше
`visibleLines` для `legend[1]` — вернуться к шагу 1 и заменить `outline` на
`edge` в обеих легендах и в документации.

- [ ] **Шаг 5: убедиться, что старого обещания не осталось**

```
rg -n "ring = crew|ring = raider|ring outlines" src tests docs
```

Ожидание: пусто.

- [ ] **Шаг 6: коммит**

```bash
git add -A
git commit -m "Легенда и документация прототипа говорят про контур, а не про кольцо"
```

---

### Задача 4: доказательство и полная проверка

**Файлы:**
- Создать: `evidence/<номер Issue>-side-indicator.json` — номер подставить
  фактический, тот же, что у Issue задачи.

- [ ] **Шаг 1: снять кадр «до»**

На чистой `origin/main`, во втором worktree:

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-game.ps1 -Fixture prepared -ScreenshotTicks 1670 -ScreenshotPath "side/before.png" -TileSize 40 -CameraZoom 2.0 -CameraPosition "900,500" -UiScale 1.0 -FrameSize "1280x720"
```

- [ ] **Шаг 2: записать доказательство**

Создать `evidence/<номер Issue>-side-indicator.json`:

```json
{
  "issue": "<номер Issue>",
  "scene": {
    "fixture": "prepared",
    "seed": 20260726,
    "tick": 1670,
    "tileSize": 40,
    "cameraZoom": 2.0,
    "cameraPosition": "900,500",
    "uiScale": 1.0,
    "frameSize": "1280x720"
  },
  "command": "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-game.ps1 -Fixture prepared -ScreenshotTicks 1670 -ScreenshotPath \"side/<before|after>.png\" -TileSize 40 -CameraZoom 2.0 -CameraPosition \"900,500\" -UiScale 1.0 -FrameSize \"1280x720\"",
  "ringDiameterPx": 98.18,
  "cellPx": 40,
  "note": "Кольцо стороны имело радиус 27 опорных px; при масштабе мира tile/22 и клетке 40 px это 49.09 px радиуса. Контур занимает площадь силуэта."
}
```

- [ ] **Шаг 3: полная проверка**

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```

Ожидание: все стадии зелёные.

- [ ] **Шаг 4: коммит и PR**

```bash
git add evidence
git commit -m "Доказательство смены индикатора стороны: сцена, команда и числа"
```

Тело PR — по формату `docs/engineering/AGENT_ENTRY.md`: раздел «До → Меняем →
После», что изменено, как проверено (каждое число с командой), оставшиеся риски.
Приложить оба кадра. В рисках назвать три известных ограничения спеки:
дальтонизм пары teal/красный, слипание силуэтов соседних тел одной стороны,
восемь draw-вызовов на тело и просвет копий сквозь внутренние дыры силуэта.

---

## Что этот план намеренно не делает

- **Шейдер вместо восьми копий.** Отдельная задача после базы: даёт настоящую
  alpha, убирает просвет сквозь дыры силуэта и сокращает восемь вызовов до
  одного.
- **Нейтралы и фракции.** Строка в таблице отношений плюс член перечисления,
  когда симуляция начнёт их производить.
- **Пара синий/оранжевый.** Отложена вместе с вопросом дальтонизма: синий
  проигрывает по контрасту на текущем полу, и смена палитры затрагивает пол.
