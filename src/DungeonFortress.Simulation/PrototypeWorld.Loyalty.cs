namespace DungeonFortress.Simulation;

// Three magnitudes that say what a creature is worth to the domain and what the
// domain is worth to it: fear, benefit and grudge. Slice 3 of the pitch's order
// of proof; the design contract is docs/design/SLICE_03_MOMENT_OF_TRUTH.md.
public sealed partial class PrototypeWorld
{
    /// <summary>
    /// Which of the three ledgers a term is credited to. The axis is named
    /// rather than passed as a dictionary so that a term can never be credited
    /// to the ledger it does not belong to by a slip of the caller.
    /// </summary>
    private enum LoyaltyAxis
    {
        Fear,
        Benefit,
        Grudge,
    }

    /// <summary>
    /// One creature's standing in the domain, kept as a total per axis
    /// <b>and</b> as the named terms the total was built from.
    ///
    /// <para>The duplication is deliberate and is the thing the card is checked
    /// against. The totals are what the simulation reads when it decides
    /// anything; the terms are what the player reads when they ask why. Two
    /// representations written by one method (<see cref="Accrue"/>) can be
    /// proved never to diverge, and that proof is the whole content of the
    /// promise "разбор слагаемых сходится с итогом". Deriving the total from the
    /// terms would make the promise true by construction and therefore
    /// unmeasurable — a mutant that shifts a term by one would move the total
    /// with it and nothing would go red.</para>
    /// </summary>
    private sealed class LoyaltyState
    {
        public int Fear { get; set; }

        public int Benefit { get; set; }

        public int Grudge { get; set; }

        // Ordinal-sorted so the canonical document never depends on the order
        // the terms happened to be credited in.
        public SortedDictionary<string, int> FearTerms { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, int> BenefitTerms { get; } = new(StringComparer.Ordinal);

        public SortedDictionary<string, int> GrudgeTerms { get; } = new(StringComparer.Ordinal);

        // What this creature was on the previous tick. The sweep is a function of
        // deltas of published facts, so it needs the previous reading of each of
        // them and nothing else.
        public CreatureMode PreviousMode { get; set; } = CreatureMode.Waiting;

        public int PreviousHp { get; set; }

        public int PreviousSatiety { get; set; }

        public int PreviousMartialForm { get; set; }

        public InjuryKind PreviousInjury { get; set; }

        public bool Initialised { get; set; }

        // Consecutive ticks with nothing frightening / nothing gainful, and
        // consecutive ticks of the two coercions the domain can be blamed for.
        // They are live counters of the same sort as CreatureState.IdleTicks and
        // are deliberately not published: the ledger is.
        public int QuietFearTicks { get; set; }

        public int QuietBenefitTicks { get; set; }

        public int HungryTicks { get; set; }

        public int RefusedPlaceTicks { get; set; }

        /// <summary>
        /// The three totals as they stood when the last card about this creature
        /// was opened. What a card reports as "this wave" is the difference, so
        /// the second card never repeats the first one's story.
        /// </summary>
        public int FearAtLastCard { get; set; }

        public int BenefitAtLastCard { get; set; }

        public int GrudgeAtLastCard { get; set; }

        /// <summary>
        /// Whether this creature's nerve failed since the last card about it.
        /// It is the one thing this prototype can observe a creature doing
        /// wrong, and it is what tells a deserved punishment from one the domain
        /// will be made to pay for.
        /// </summary>
        public bool PanickedSinceLastCard { get; set; }

        /// <summary>
        /// Raiders this creature put down since the last card about it. It is a
        /// deed and not a magnitude of standing, which is why it lives beside
        /// the three ledgers instead of inside one: what the domain owes
        /// somebody for killing a raider is exactly the question the player is
        /// being asked, and answering it in advance with a benefit term would
        /// be the game rewarding on the player's behalf.
        /// </summary>
        public int RaidersDownedSinceLastCard { get; set; }

        public void Remember(CreatureState creature)
        {
            PreviousMode = creature.Mode;
            PreviousHp = creature.Hp;
            PreviousSatiety = creature.Satiety;
            PreviousMartialForm = creature.MartialForm;
            PreviousInjury = creature.Injury;
            Initialised = true;
        }
    }

    /// <summary>
    /// Credits one named term to one axis of one creature, moving the total and
    /// the ledger entry by the same amount. Every change to any of the three
    /// magnitudes in the whole simulation goes through here — there is no second
    /// door — which is what makes "the breakdown adds up to the number" a
    /// property of the code rather than a habit.
    /// </summary>
    /// <returns><c>true</c> when the term actually moved anything.</returns>
    private static bool Accrue(
        CreatureState creature,
        LoyaltyAxis axis,
        string code,
        int amount)
    {
        if (amount == 0)
        {
            return false;
        }

        var ledger = creature.Loyalty;
        switch (axis)
        {
            case LoyaltyAxis.Fear:
                ledger.Fear += amount;
                ledger.FearTerms[code] = ledger.FearTerms.GetValueOrDefault(code) + amount;
                break;
            case LoyaltyAxis.Benefit:
                ledger.Benefit += amount;
                ledger.BenefitTerms[code] = ledger.BenefitTerms.GetValueOrDefault(code) + amount;
                break;
            case LoyaltyAxis.Grudge:
                ledger.Grudge += amount;
                ledger.GrudgeTerms[code] = ledger.GrudgeTerms.GetValueOrDefault(code) + amount;
                break;
            default:
                throw new InvalidDataException($"Unknown loyalty axis: {axis}");
        }

        return true;
    }

    /// <summary>
    /// How much of this creature's grudge is currently visible in its
    /// behaviour: whatever the fear no longer covers.
    ///
    /// <para>Section 6.3 of the pitch: "Пока страх высок, обида не видна. Как
    /// только он падает … накопленное выстреливает разом". So the grudge is
    /// always accumulated and only sometimes acted on, and what holds it down is
    /// the creature's own fear rather than a timer.</para>
    ///
    /// <para><b>A comparison and not a threshold, and the difference is
    /// load-bearing.</b> The first version released the whole grudge below a
    /// fixed fear and nothing above it, which made the mechanic unreachable from
    /// its own main source: a punishment is the largest single grudge there is
    /// and it raises fear by more than it raises the grudge, so a punished
    /// creature was pinned on the wrong side of the threshold for the rest of
    /// the party. Independent review of PR #328 measured the consequence —
    /// <c>combat_refused_grudge</c> never fired in any shipped journal.
    /// Subtracting one from the other makes the delayed price arrive when the
    /// pitch says it does: fear fades one point per
    /// <see cref="PrototypeTuning.LoyaltyFearFadePeriod"/> quiet ticks, and the
    /// resentment underneath it surfaces as it goes.</para>
    /// </summary>
    private static int ReleasedGrudge(CreatureState creature) =>
        Math.Max(0, creature.Loyalty.Grudge - creature.Loyalty.Fear);

    /// <summary>
    /// The one sweep that credits everything the world itself produced this
    /// tick. It reads published facts only — mode, health, satiety, martial
    /// form, injury, the refusal by memory of place and the state of the larder
    /// — and compares them against the same facts a tick ago.
    ///
    /// <para><b>Why a sweep and not a hook at every event.</b> Two reasons, and
    /// the second is the load-bearing one. A sweep puts every accrual in one
    /// file, so a mutant that zeroes one term is a one-line edit at a named
    /// place and the whole of the mechanic has one reading order. And it keeps
    /// loyalty a function of what the domain published rather than of internal
    /// call sites: a magnitude the player is asked to reason about must be
    /// derivable from what the player can see.</para>
    ///
    /// <para>Verdict terms are the exception and are credited by
    /// <see cref="ApplyVerdict"/> at the tick of the command, because a verdict
    /// is not something the world did — it is the one thing the player did.</para>
    /// </summary>
    private void AccrueLoyalty()
    {
        // Read once, before anything is committed: who went down on this very
        // tick. Doing it inside the per-creature loop would make the answer
        // depend on how far the loop had already got.
        var downedThisTick = _creatures
            .Where(creature =>
                creature.Mode == CreatureMode.Downed &&
                creature.Loyalty.Initialised &&
                creature.Loyalty.PreviousMode != CreatureMode.Downed)
            .ToArray();

        foreach (var creature in _creatures.OrderBy(creature => creature.Id))
        {
            var ledger = creature.Loyalty;
            if (!ledger.Initialised)
            {
                ledger.Remember(creature);
                continue;
            }

            var frightened = AccrueFear(creature, downedThisTick);
            var gained = AccrueBenefit(creature);
            AccrueGrudge(creature);
            FadeFear(creature, frightened);
            FadeBenefit(creature, gained);
        }

        foreach (var creature in _creatures)
        {
            creature.Loyalty.Remember(creature);
        }
    }

    /// <summary>
    /// What frightened this creature since the previous tick: being put down,
    /// breaking, taking a blow, and watching somebody near it fall.
    /// </summary>
    private bool AccrueFear(CreatureState creature, IReadOnlyList<CreatureState> downedThisTick)
    {
        var ledger = creature.Loyalty;
        var frightened = false;
        if (creature.Mode == CreatureMode.Downed && ledger.PreviousMode != CreatureMode.Downed)
        {
            frightened |= Accrue(
                creature, LoyaltyAxis.Fear, "fear_wound", PrototypeTuning.LoyaltyFearWound);
        }

        if (creature.Mode == CreatureMode.Fled && ledger.PreviousMode != CreatureMode.Fled)
        {
            ledger.PanickedSinceLastCard = true;
            frightened |= Accrue(
                creature, LoyaltyAxis.Fear, "fear_panic", PrototypeTuning.LoyaltyFearPanic);
        }

        // Only what this creature could see from where it is standing, by the
        // same radius morale already uses. A domain-wide count would put the
        // whole population on one number again, which is the herd Issue #101
        // removed.
        var witnessed = downedThisTick.Count(other =>
            other != creature &&
            Manhattan(creature.Position, other.Position) <= PrototypeTuning.MoraleWitnessRadius);
        if (witnessed > 0)
        {
            frightened |= Accrue(
                creature,
                LoyaltyAxis.Fear,
                "fear_ally_downed",
                PrototypeTuning.LoyaltyFearAllyDowned * witnessed);
        }

        return frightened;
    }

    /// <summary>
    /// What the domain gave this creature since the previous tick: a portion
    /// eaten, form gained at a post, a wound tended.
    ///
    /// <para><b>Training is deliberately not a benefit</b>, and the omission is
    /// both a principle and a measurement. The principle: <c>martialForm</c> is
    /// what a creature gains from its own labour, and it pays for it in satiety
    /// and fatigue (<see cref="PrototypeTuning.DrillSatietyCost"/>,
    /// <see cref="PrototypeTuning.DrillFatigue"/>) — what the domain gives is a
    /// portion, a bunk and a verdict, not the work somebody did. The
    /// measurement: with training counted, `prepared` carried up to 29 points of
    /// benefit before the first wave had even landed against `baseline`'s one, so
    /// the ledger of a drilling domain was thirty deep in routine and the twelve
    /// points a reward is worth were lost inside it. Both readings say the same
    /// thing: a reward has to be legible against the ledger it lands in.</para>
    ///
    /// <para>"Выполненная работа" from the orienting list of Issue #312 is
    /// deliberately absent. There is no published per-creature fact that says "a
    /// job finished on this tick" — <c>workTicks</c> counts ticks and not
    /// completions — and inventing one would mean writing into
    /// <c>PrototypeWorld.Work.cs</c>, which the partition of the task does not
    /// give this work. Naming the gap is cheaper than an accrual nobody can
    /// check.</para>
    /// </summary>
    private static bool AccrueBenefit(CreatureState creature)
    {
        var ledger = creature.Loyalty;
        var gained = false;
        if (creature.Satiety > ledger.PreviousSatiety)
        {
            gained |= Accrue(
                creature, LoyaltyAxis.Benefit, "benefit_fed", PrototypeTuning.LoyaltyBenefitFed);
        }

        if (creature.Injury < ledger.PreviousInjury)
        {
            gained |= Accrue(
                creature, LoyaltyAxis.Benefit, "benefit_tended", PrototypeTuning.LoyaltyBenefitTended);
        }

        return gained;
    }

    /// <summary>
    /// The delayed price of fear (pitch 6.3). Every term here is a coercion —
    /// something the creature put up with because it was afraid — and every one
    /// of them is therefore gated on the fear that bought the compliance: below
    /// <see cref="PrototypeTuning.LoyaltyGrudgeFearFloor"/> nothing is credited
    /// at all, so grudge cannot become a third independent scale.
    ///
    /// <para>The one term outside this sweep that is <b>not</b> gated is
    /// <c>grudge_ignored</c>, credited when a card closes unanswered. It is
    /// named as an exception in the design contract rather than smuggled in:
    /// ADR 0019 requires the absence of a verdict to have a consequence, and
    /// silence is not a coercion.</para>
    /// </summary>
    private void AccrueGrudge(CreatureState creature)
    {
        var ledger = creature.Loyalty;
        if (ledger.Fear < PrototypeTuning.LoyaltyGrudgeFearFloor)
        {
            ledger.HungryTicks = 0;
            ledger.RefusedPlaceTicks = 0;
            return;
        }

        // Hungry while the larder is not empty: the domain had supper and this
        // one did not get it.
        if (creature.Satiety < PrototypeTuning.EatThreshold &&
            creature.Mode != CreatureMode.Downed &&
            _stockMeals > 0)
        {
            ledger.HungryTicks++;
            if (ledger.HungryTicks >= PrototypeTuning.LoyaltyGrudgeHungerPeriod)
            {
                ledger.HungryTicks = 0;
                Accrue(
                    creature,
                    LoyaltyAxis.Grudge,
                    "grudge_hunger",
                    PrototypeTuning.LoyaltyGrudgeHunger);
            }
        }
        else
        {
            ledger.HungryTicks = 0;
        }

        // Work refused because of where it would have started (ADR 0018). The
        // creature pays for the fright the domain led it into, and it goes on
        // paying for as long as the refusal lasts.
        if (creature.AvoidedThisTick is not null)
        {
            ledger.RefusedPlaceTicks++;
            if (ledger.RefusedPlaceTicks >= PrototypeTuning.LoyaltyGrudgeRefusedPlacePeriod)
            {
                ledger.RefusedPlaceTicks = 0;
                Accrue(
                    creature,
                    LoyaltyAxis.Grudge,
                    "grudge_refused_place",
                    PrototypeTuning.LoyaltyGrudgeRefusedPlace);
            }
        }
        else
        {
            ledger.RefusedPlaceTicks = 0;
        }
    }

    /// <summary>
    /// Fear fades in quiet. It is the mechanism section 6.3 of the pitch needs
    /// and not a convenience: a grudge that is only visible while fear is low
    /// needs fear to be able to fall, otherwise the second half of the mechanic
    /// never happens.
    ///
    /// <para>The fade is itself a term, with a negative amount, so the ledger
    /// still adds up to the total and the player can read "страх 12 (+16 бой,
    /// −4 забылось)" instead of watching a number drift for no stated
    /// reason.</para>
    /// </summary>
    private static void FadeFear(CreatureState creature, bool frightened)
    {
        var ledger = creature.Loyalty;
        if (frightened)
        {
            ledger.QuietFearTicks = 0;
            return;
        }

        if (ledger.Fear <= 0)
        {
            ledger.QuietFearTicks = 0;
            return;
        }

        ledger.QuietFearTicks++;
        if (ledger.QuietFearTicks < PrototypeTuning.LoyaltyFearFadePeriod)
        {
            return;
        }

        ledger.QuietFearTicks = 0;
        Accrue(creature, LoyaltyAxis.Fear, "fear_faded", -1);
    }

    /// <summary>
    /// Gratitude fades the same way and for the same reason, one point at a
    /// time. It is what makes the effect of every verdict reversible by ordinary
    /// play — the fifth condition of admissibility of a <c>verdict</c> value
    /// (Issue #167) — without a cancelling command, which ADR 0019 forbids.
    /// </summary>
    private static void FadeBenefit(CreatureState creature, bool gained)
    {
        var ledger = creature.Loyalty;
        if (gained || ledger.Benefit <= 0)
        {
            ledger.QuietBenefitTicks = 0;
            return;
        }

        ledger.QuietBenefitTicks++;
        if (ledger.QuietBenefitTicks < PrototypeTuning.LoyaltyBenefitFadePeriod)
        {
            return;
        }

        ledger.QuietBenefitTicks = 0;
        Accrue(creature, LoyaltyAxis.Benefit, "benefit_faded", -1);
    }

    /// <summary>
    /// Accumulated resentment is spent when it is acted on: the pitch's
    /// "накопленное выстреливает разом" is a discharge and not a permanent
    /// deduction from the creature's willingness. Spending is a negative term of
    /// the same ledger, so the breakdown still adds up.
    /// </summary>
    private static void SpendGrudge(CreatureState creature)
    {
        var spent = Math.Min(creature.Loyalty.Grudge, PrototypeTuning.LoyaltyGrudgeDischarge);
        if (spent > 0)
        {
            Accrue(creature, LoyaltyAxis.Grudge, "grudge_spent", -spent);
        }
    }

    /// <summary>
    /// What loyalty adds to the score of a piece of work. Bounded on both sides
    /// by <see cref="PrototypeTuning.LoyaltyWorkBiasCap"/>, which is far below
    /// one step of priority (<see cref="PrototypeTuning.ScorePriorityWeight"/>),
    /// so loyalty can move a creature between two comparable jobs and can never
    /// override what the player asked the domain to care about. That bound is
    /// the executable half of "ни одно значение не делает ни одно поведение
    /// неизбежным".
    ///
    /// <para><b>Fear and released resentment, and they are the two halves of one
    /// sentence of the pitch.</b> Section 6.3: "Страх работает немедленно: под
    /// страхом существа делают то, чего не хотят — работают голодными … терпят
    /// несправедливость. Но каждое такое принуждение копит обиду." So fear makes
    /// a creature readier to take what it is offered, resentment makes it drag,
    /// and the second is bought by the first. Both are bounded by the same cap,
    /// which sits below one step of affinity, so neither can outrank what the
    /// player asked the domain to care about.</para>
    ///
    /// <para>Neither exists before the first fight, so a domain that has not been
    /// raided yet chooses its work exactly as it did before this mechanic. What
    /// benefit does to work is a different thing and has its own function,
    /// <see cref="LoyaltyReach"/>.</para>
    /// </summary>
    private static int LoyaltyWorkBias(CreatureState creature)
    {
        var bias =
            creature.Loyalty.Fear / PrototypeTuning.LoyaltyWorkFearDivisor -
            ReleasedGrudge(creature) / PrototypeTuning.LoyaltyWorkGrudgeDivisor;
        return Math.Clamp(
            bias,
            -PrototypeTuning.LoyaltyWorkBiasCap,
            PrototypeTuning.LoyaltyWorkBiasCap);
    }

    /// <summary>
    /// How many tiles of the way to a job this creature is willing to stop
    /// counting, because the domain has given it something.
    ///
    /// <para><b>Why benefit needed a second channel at all.</b> Independent
    /// review of PR #328 found the two verdicts asymmetric: the grudge a
    /// punishment buys was read in two places, and benefit in exactly one — the
    /// holding side of <see cref="ResentmentOutweighsTheLine"/>, which is a
    /// contest that almost never runs. So <c>reward</c> was a command with no
    /// observable consequence, and criterion 7 of Issue #312 held for one value
    /// of the enumeration out of two. The owner's decision of 2026-08-07 is that
    /// benefit gets a second reading in behaviour, and names the shape:
    /// готовность браться за тяжёлую или далёкую работу.</para>
    ///
    /// <para><b>Why distance and not nerve.</b> The symmetric answer — a term in
    /// <see cref="ApplyMorale"/>, mirroring the grudge — was rejected, and for a
    /// reason already measured on this branch rather than a taste: loyalty in
    /// nerve was tried with fear and again with the grudge, and both times it
    /// paid with an invariant (contract invariant 4 on seed 20260728, and the
    /// readability of panic on prepared/20260727). Nerve answers "will it hold
    /// when it is frightened", and a reward is not an answer to fear. Distance
    /// answers "will it walk over there for you", which is what a creature that
    /// owes the domain something actually does differently on an ordinary day —
    /// and an ordinary day is where the pitch's section 6.11 puts the weight of
    /// a reward.</para>
    ///
    /// <para>It forgives distance and never adds score: a creature that has been
    /// treated well is willing to walk further, not to work harder in place. The
    /// cap keeps the forgiveness below the reach of the map, so the far corner
    /// never becomes as cheap as the next room.</para>
    /// </summary>
    private static int LoyaltyReach(CreatureState creature) =>
        Math.Min(
            creature.Loyalty.Benefit / PrototypeTuning.LoyaltyWorkReachDivisor,
            PrototypeTuning.LoyaltyWorkReachCap);

    private PrototypeLoyaltySnapshot ToSnapshot(LoyaltyState ledger, bool released) =>
        new(
            ledger.Fear,
            ledger.Benefit,
            ledger.Grudge,
            ToTerms(ledger.FearTerms),
            ToTerms(ledger.BenefitTerms),
            ToTerms(ledger.GrudgeTerms),
            released);

    private static IReadOnlyList<PrototypeLoyaltyTerm> ToTerms(
        SortedDictionary<string, int> terms) =>
        [.. terms.Select(pair => new PrototypeLoyaltyTerm(pair.Key, pair.Value))];
}
