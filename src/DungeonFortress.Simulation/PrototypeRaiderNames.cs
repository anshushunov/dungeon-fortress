namespace DungeonFortress.Simulation;

/// <summary>
/// Where a raider's name comes from (Issue #358, slice 5 of the pitch's order of
/// proof).
///
/// <para>A name is drawn from one closed pool through the party's own
/// <see cref="DeterministicRandom"/> stream and from nothing else. No
/// <c>System.Random</c>, no clock, no <c>Guid</c>: the same seed and the same
/// command log must produce the same canonical snapshot byte for byte, and a name
/// that is in that snapshot is bound by that rule exactly like a tile is.</para>
///
/// <para><b>Names are unique inside one party, and that is bought rather than
/// assumed.</b> The pool is finite and a party can field more raiders than it has
/// entries — <c>T.wave_max_raiders</c> = 12 over <c>T.wave_count</c> = 4 waves is
/// 48 against 24 nicknames — so a plain draw would hand two raiders the same name
/// and the whole slice would stop being able to say "this is the one you already
/// met". A taken nickname therefore takes an epithet, drawn from the same stream:
/// «Крюк», then «Крюк Рыжий». Twenty-four by nine is two hundred and sixteen
/// forms, which is above any party this prototype can play.</para>
/// </summary>
public static class PrototypeRaiderNames
{
    /// <summary>
    /// The salt of the naming stream. It is its own stream and not the combat one
    /// on purpose: naming a raider must not move the jitter of anybody's blow, so
    /// that a party where a name was added reads the same fight as one where it
    /// was not.
    /// </summary>
    public const ulong StreamSalt = 0x6E616D6572UL;

    /// <summary>
    /// What the raiders call each other. Twenty-four, in a register of their own:
    /// the domain's people are named after things that endure — Кремень, Брусок,
    /// Смола — and the people who come through the gate are named after what they
    /// carry and what has already been done to them.
    /// </summary>
    public static IReadOnlyList<string> Nicknames { get; } =
    [
        "Крюк",
        "Гвоздь",
        "Клык",
        "Заноза",
        "Кистень",
        "Обрубок",
        "Секира",
        "Плешь",
        "Бельмо",
        "Сиплый",
        "Долговязый",
        "Рваный",
        "Кривой",
        "Хромой",
        "Тощий",
        "Ржавый",
        "Бурый",
        "Косой",
        "Пепел",
        "Хват",
        "Ловчий",
        "Гнилозуб",
        "Вислоух",
        "Черпак",
    ];

    /// <summary>
    /// What a second raider of the same nickname is told apart by. Nine, so a
    /// third and a fourth «Крюк» still have somewhere to go.
    /// </summary>
    public static IReadOnlyList<string> Epithets { get; } =
    [
        "Рыжий",
        "Старший",
        "Младший",
        "Немой",
        "Одноухий",
        "Щербатый",
        "Тихий",
        "Злой",
        "Меньшой",
    ];

    /// <summary>
    /// The number of distinct names this pool can produce, which is the bound the
    /// uniqueness rule rests on.
    /// </summary>
    public static int Capacity => Nicknames.Count * (Epithets.Count + 1);
}
