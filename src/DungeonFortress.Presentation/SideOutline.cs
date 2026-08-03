namespace DungeonFortress.Presentation;

/// <summary>
/// Что тело значит для игрока. Канал несёт именно отношение, а не то, из какой
/// существо фракции: пак рас общий, поэтому на карте возможен гоблин-свой
/// против гоблина-чужого, и различать стороны нужно независимо от расы. Какая
/// именно фракция — вторичный вопрос, его место в инспекторе и тултипе.
///
/// <para>
/// Нейтрала здесь нет намеренно: симуляция не производит ни одного, а
/// недостижимую ветку нельзя протестировать. Когда нейтралы появятся,
/// добавление члена заставит компилятор перечислить все места, где решение
/// принимается.
/// </para>
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
/// причине <see href="../../docs/decisions/0011-presentation-layer-without-engine.md">ADR
/// 0011</see>: адаптер не собирается джобом «Pure .NET», поэтому значение,
/// решённое там, решено там, где его никто не проверяет.
///
/// <para>
/// Заменяет кольцо стороны Issue #177. То кольцо было видимым, но при клетке
/// 40 px имело радиус <c>27 * 40 / 22 = 49.09</c> px и диаметр 98.18 px против
/// 40 px клетки, которую занимает одно тело, — около шести клеток площади на
/// одно существо. Контур занимает площадь силуэта, поэтому плотность толпы его
/// не ломает. Полное обоснование и отвергнутые варианты — в
/// <c>docs/design/SIDE_INDICATOR.md</c>.
/// </para>
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
