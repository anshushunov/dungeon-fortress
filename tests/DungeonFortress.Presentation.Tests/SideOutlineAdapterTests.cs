using Xunit;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// Структурная половина спеки docs/design/SIDE_INDICATOR.md: таблица отношений
/// сама по себе — правило ни о чём, пока адаптер по ней не рисует. Здесь
/// читается исходник Main.cs тем же читателем, что и в
/// <see cref="WorldDrawPassGuardTests"/>: движок не запускается (ADR 0011).
///
/// Заменяет <c>SideMarkerVisibilityTests</c>, который держал геометрию кольца —
/// кольцо снято, и его условие перестало что-либо означать.
/// </summary>
public sealed class SideOutlineAdapterTests
{
    /// <summary>
    /// Оба вида тел рисуются через общую рутину и ни один не рисует кольцо: у
    /// каждого ровно один вызов <c>DrawSidedBody</c>, ни одного <c>DrawArc</c>
    /// и ни одного прямого <c>DrawGoblin</c>.
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
    /// <para>
    /// Читатель различает <c>DrawGoblin(</c> и <c>DrawGoblinOutline(</c>: за
    /// именем первого идёт символ, годный в идентификатор, и такой вызов
    /// пропускается.
    /// </para>
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
    /// Цвет, ширина и смещения контура берутся из таблицы отношений, а не из
    /// литерала рядом. Литерал невидим для любой проверки в репозитории — тот
    /// же довод, которым живёт проверка альфы в WorldDrawPassGuardTests.
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
