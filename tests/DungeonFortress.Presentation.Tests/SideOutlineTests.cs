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
    /// Сколько world-пикселей бахромы должно остаться снаружи тела, чтобы
    /// игрок её увидел.
    ///
    /// <para>
    /// Это то условие, которое держал <c>SideMarkerVisibilityTests</c> для
    /// кольца и которое иначе потерялось бы вместе с ним. Режим отказа у
    /// Issue #177 и у контура один: индикатор есть в коде, а игрок его не
    /// видит, — меняется только причина. У кольца причиной было перекрытие
    /// спрайтом, у контура это толщина: копии силуэта рисуются ПОД спрайтом,
    /// поэтому наружу выходит ровно ширина смещения и ничего больше. Без
    /// этого пола ширины 0.1 и 0.2 опорных px проходят все остальные
    /// проверки, а при клетке 40 это бахрома 0.18 world px.
    /// </para>
    ///
    /// <para>
    /// 1.5 px, а не 1.0: мир рисуется фильтром <c>LinearWithMipmaps</c> (поле
    /// <c>textureFilter</c> в диагностике кадра), поэтому штрих тоньше
    /// полутора пикселей усредняется с фоном раньше, чем доходит до игрока.
    /// Самое узкое место — свой контур при наименьшей поддерживаемой клетке
    /// 32: <c>1.2 * 32 / 22 = 1.745</c> px, запас над полом 0.245 px. Запас
    /// маленький намеренно: пол описывает границу видимости, а не удобную
    /// дистанцию от текущих значений.
    /// </para>
    /// </summary>
    private const double MinimumVisibleFringeWorldPx = 1.5;

    /// <summary>
    /// Контур каждого отношения остаётся видимым при каждом размере клетки,
    /// который поддерживает игра: 32, 40 и 48 px (диапазон ADR 0008).
    ///
    /// <para>
    /// Если тест покраснел — сторона перестала читаться, и чинить это надо
    /// увеличением ширины в <see cref="SideOutline"/>, а не правкой пола
    /// здесь.
    /// </para>
    /// </summary>
    [Fact]
    public void The_outline_stays_visible_at_every_supported_tile_size()
    {
        foreach (var tileSize in new[] { CameraView.MinimumTileSize, 40, CameraView.MaximumTileSize })
        {
            foreach (var relation in Enum.GetValues<BodyRelation>())
            {
                var fringePx = SideOutline.WidthRef(relation) *
                    CameraView.WorldVisualScale(tileSize);

                Assert.True(
                    fringePx >= MinimumVisibleFringeWorldPx,
                    $"Tile {tileSize}, relation {relation}: the outline shows " +
                    $"{fringePx:f2} px of fringe, under the {MinimumVisibleFringeWorldPx:f2} px " +
                    "floor. The side is in the code and invisible on screen — " +
                    "the failure mode of Issue #177 with a different cause.");
            }
        }
    }

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
