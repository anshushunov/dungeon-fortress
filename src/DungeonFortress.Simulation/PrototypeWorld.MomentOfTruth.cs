namespace DungeonFortress.Simulation;

// The pause between two waves: three cards, one verdict apiece, and what the
// absence of a verdict costs. Slice 3 of the pitch's order of proof; the design
// contract is docs/design/SLICE_03_MOMENT_OF_TRUTH.md.
public sealed partial class PrototypeWorld
{
    /// <summary>
    /// The wave that has just been resolved and is owed a moment of truth. Set
    /// inside the tick by <see cref="ResolveWave"/> and consumed at the end of
    /// it, so the cards are built from a finished tick rather than from the
    /// middle of one.
    /// </summary>
    private WaveState? _pendingMomentOfTruth;

    private MomentOfTruthState? _momentOfTruth;

    private sealed class MomentOfTruthState(int waveNumber, int openedTick)
    {
        public int WaveNumber { get; } = waveNumber;

        public int OpenedTick { get; } = openedTick;

        public int WaitedSteps { get; set; }

        public List<MomentOfTruthCard> Cards { get; } = [];

        public bool Answered => Cards.TrueForAll(card => card.Verdict is not null);
    }

    private sealed class MomentOfTruthCard(
        CreatureState creature,
        int fearThisWave,
        int benefitThisWave,
        int grudgeThisWave,
        int raidersDowned,
        string dominantAxis,
        int notability)
    {
        public CreatureState Creature { get; } = creature;

        public int FearThisWave { get; } = fearThisWave;

        public int BenefitThisWave { get; } = benefitThisWave;

        public int GrudgeThisWave { get; } = grudgeThisWave;

        public int RaidersDowned { get; } = raidersDowned;

        public string DominantAxis { get; } = dominantAxis;

        public int Notability { get; } = notability;

        public VerdictKind? Verdict { get; set; }
    }

    /// <summary>
    /// Whether the party is standing still waiting for the player. While this is
    /// true no tick of the world happens at all: <see cref="CurrentTick"/> does
    /// not move, nothing is decided, nothing is eaten and nothing grows.
    /// </summary>
    public bool IsAwaitingVerdict => _momentOfTruth is not null;

    /// <summary>
    /// One step of a party that is waiting rather than playing. Commands due on
    /// the frozen tick are applied — that is how a verdict arrives — and if the
    /// pause is still open afterwards the step is spent waiting.
    ///
    /// <para>The window closes by itself after
    /// <see cref="PrototypeTuning.MomentOfTruthWindowSteps"/> steps. It has to:
    /// a party that could not be played through without a verdict would make
    /// every shipped fixture, every determinism run and every load run hang. The
    /// closing is not a shrug — ADR 0019 requires the absence of a verdict to be
    /// observable ("окно карточки закрывается в известный тик"), and
    /// <see cref="CloseMomentOfTruth"/> is where that consequence is paid.</para>
    /// </summary>
    /// <returns><c>true</c> when this step was spent waiting and no tick ran.</returns>
    private bool StepWhileAwaitingVerdict()
    {
        if (_momentOfTruth is not { } pause)
        {
            return false;
        }

        ApplyCommands();
        if (_momentOfTruth is null)
        {
            // Every card was answered on this step: the pause is over and the
            // tick it was holding back may run now.
            return false;
        }

        pause.WaitedSteps++;
        if (pause.WaitedSteps >= PrototypeTuning.MomentOfTruthWindowSteps)
        {
            CloseMomentOfTruth();
        }

        return true;
    }

    /// <summary>
    /// Opens the pause the wave just resolved is owed, if it is owed one.
    ///
    /// <para>Two waves are deliberately not followed by a card. The last one is
    /// not, because the whole promise of the slice is that the consequence is
    /// visible <b>in the next wave</b>, and after the last one there is none; and
    /// a fallen domain is not, because there is nobody left to judge and nothing
    /// left to judge them for.</para>
    /// </summary>
    private void OpenPendingMomentOfTruth()
    {
        if (_pendingMomentOfTruth is not { } wave)
        {
            return;
        }

        _pendingMomentOfTruth = null;
        if (_sessionOutcome is not null || wave.Number >= _waves.Count)
        {
            return;
        }

        var pause = new MomentOfTruthState(wave.Number, CurrentTick);
        foreach (var card in SelectCards())
        {
            pause.Cards.Add(card);
        }

        foreach (var card in pause.Cards)
        {
            var ledger = card.Creature.Loyalty;
            ledger.FearAtLastCard = ledger.Fear;
            ledger.BenefitAtLastCard = ledger.Benefit;
            ledger.GrudgeAtLastCard = ledger.Grudge;
            ledger.RaidersDownedSinceLastCard = 0;
        }

        _momentOfTruth = pause;
    }

    /// <summary>
    /// Which three creatures the domain reports on, and in which order.
    ///
    /// <para>The rule, stated so that it can be checked rather than read:</para>
    ///
    /// <list type="number">
    /// <item><description><b>Notability</b> of a creature is
    /// <see cref="PrototypeTuning.MomentOfTruthDeedWeight"/> per raider it put
    /// down since the last card about it, plus the largest of the three amounts
    /// its fear, its benefit and its grudge have moved by over the same stretch.
    /// The magnitudes are read as deltas and not as totals, so the second card
    /// never repeats the first one's story.</description></item>
    /// <item><description>The <b>dominant axis</b> is what the card is about:
    /// <c>deed</c> if this creature put a raider down — that is the case
    /// ADR 0019 names, «он убил героя, а ты не наградил» — and otherwise the
    /// axis the delta came from, ties broken in the fixed order benefit, fear,
    /// grudge: what the domain owes, then what it frightened, then what it
    /// soured.</description></item>
    /// <item><description>Creatures are ordered by notability descending, then by
    /// id ascending, and the first
    /// <see cref="PrototypeTuning.MomentOfTruthCards"/> of that order are the
    /// cards. Nothing here reads a dictionary, a hash or the random stream, so
    /// two runs of one seed produce the same three cards in the same
    /// order.</description></item>
    /// <item><description>If fewer than three creatures moved at all, the list is
    /// filled from the same order — a domain that reports on two creatures and
    /// then on three would be reporting on how eventful the wave was rather than
    /// on its people.</description></item>
    /// </list>
    /// </summary>
    private IEnumerable<MomentOfTruthCard> SelectCards()
    {
        return _creatures
            .Select(creature =>
            {
                var ledger = creature.Loyalty;
                var fear = ledger.Fear - ledger.FearAtLastCard;
                var benefit = ledger.Benefit - ledger.BenefitAtLastCard;
                var grudge = ledger.Grudge - ledger.GrudgeAtLastCard;
                var standing = Math.Max(benefit, Math.Max(fear, grudge));
                var deeds = ledger.RaidersDownedSinceLastCard;
                var notability = deeds * PrototypeTuning.MomentOfTruthDeedWeight + standing;
                var dominant = deeds > 0
                    ? "deed"
                    : benefit == standing
                        ? "benefit"
                        : fear == standing
                            ? "fear"
                            : "grudge";
                return new MomentOfTruthCard(
                    creature, fear, benefit, grudge, deeds, dominant, notability);
            })
            .OrderByDescending(card => card.Notability)
            .ThenBy(card => card.Creature.Id)
            .Take(PrototypeTuning.MomentOfTruthCards);
    }

    /// <summary>
    /// The player answers one card. Called from
    /// <see cref="ApplyCommand"/> on the tick of the command, because that is
    /// where the authority for "is the window open and was there a card about
    /// this one" lives — the static pre-flight has no world (ADR 0019).
    ///
    /// <para>Every refusal throws before anything is written, so a rejected
    /// verdict leaves the world exactly as it found it: the atomicity of
    /// ADR 0005 holds for the new command word for word.</para>
    /// </summary>
    private void ApplyVerdict(VerdictCommand command)
    {
        if (_momentOfTruth is not { } pause)
        {
            throw new InvalidDataException(
                "A verdict is only accepted while the moment of truth is open.");
        }

        var card = pause.Cards.FirstOrDefault(item => item.Creature.Id == command.CreatureId);
        if (card is null)
        {
            throw new InvalidDataException(
                $"The domain reported no card about creature {command.CreatureId}; " +
                "a verdict may only answer a card.");
        }

        if (card.Verdict is not null)
        {
            throw new InvalidDataException(
                $"Creature {command.CreatureId} has already been answered in this " +
                "moment of truth; a verdict is a single act.");
        }

        card.Verdict = command.Verdict;
        var creature = card.Creature;
        switch (command.Verdict)
        {
            case VerdictKind.Reward:
                Accrue(
                    creature,
                    LoyaltyAxis.Benefit,
                    "benefit_rewarded",
                    PrototypeTuning.LoyaltyVerdictRewardBenefit);
                RecordDecision(
                    creature,
                    "verdict_rewarded",
                    new Dictionary<string, int>
                    {
                        ["wave"] = pause.WaveNumber,
                        ["benefit"] = creature.Loyalty.Benefit,
                    });
                break;
            case VerdictKind.Punish:
                Accrue(
                    creature,
                    LoyaltyAxis.Fear,
                    "fear_punished",
                    PrototypeTuning.LoyaltyVerdictPunishFear);
                // A punishment that lands on somebody who did not break is the
                // textbook coercion of pitch 6.3: it works now and is paid for
                // later. "Fault" is the one thing this prototype can observe a
                // creature doing wrong — its nerve failed and it left the line.
                var atFault = creature.Loyalty.PanickedSinceLastCard;
                if (!atFault)
                {
                    Accrue(
                        creature,
                        LoyaltyAxis.Grudge,
                        "grudge_punished_unfairly",
                        PrototypeTuning.LoyaltyVerdictPunishUnfairGrudge);
                }

                RecordDecision(
                    creature,
                    atFault ? "verdict_punished" : "verdict_punished_without_fault",
                    new Dictionary<string, int>
                    {
                        ["wave"] = pause.WaveNumber,
                        ["fear"] = creature.Loyalty.Fear,
                        ["grudge"] = creature.Loyalty.Grudge,
                    });
                break;
            default:
                throw new InvalidDataException($"Unknown verdict: {command.Verdict}");
        }

        creature.Loyalty.PanickedSinceLastCard = false;
        if (pause.Answered)
        {
            CloseMomentOfTruth();
        }
    }

    /// <summary>
    /// Closes the window and charges the domain for what it did not say.
    ///
    /// <para>Silence costs only where the domain itself said there was something
    /// to answer: a card whose dominant axis is <c>benefit</c> is the domain
    /// reporting that this creature earned something. ADR 0019 states the case
    /// literally — «+2 за то, что он убил героя, а ты не наградил» — and this is
    /// that clause. A card about somebody who was merely frightened costs
    /// nothing to leave alone.</para>
    /// </summary>
    private void CloseMomentOfTruth()
    {
        if (_momentOfTruth is not { } pause)
        {
            return;
        }

        foreach (var card in pause.Cards
                     .Where(card => card.Verdict is null && card.DominantAxis == "deed")
                     .OrderBy(card => card.Creature.Id))
        {
            Accrue(
                card.Creature,
                LoyaltyAxis.Grudge,
                "grudge_ignored",
                PrototypeTuning.LoyaltyGrudgeIgnored);
            RecordDecision(
                card.Creature,
                "verdict_ignored",
                new Dictionary<string, int>
                {
                    ["wave"] = pause.WaveNumber,
                    ["grudge"] = card.Creature.Loyalty.Grudge,
                });
        }

        foreach (var card in pause.Cards)
        {
            card.Creature.Loyalty.PanickedSinceLastCard = false;
        }

        _momentOfTruth = null;
    }

    private PrototypeMomentOfTruthSnapshot ToMomentOfTruthSnapshot()
    {
        if (_momentOfTruth is not { } pause)
        {
            return new PrototypeMomentOfTruthSnapshot(
                false, 0, 0, 0, PrototypeTuning.MomentOfTruthWindowSteps, []);
        }

        return new PrototypeMomentOfTruthSnapshot(
            true,
            pause.WaveNumber,
            pause.OpenedTick,
            pause.WaitedSteps,
            PrototypeTuning.MomentOfTruthWindowSteps,
            [
                .. pause.Cards.Select(card => new PrototypeMomentOfTruthCard(
                    card.Creature.Id,
                    card.Creature.Name,
                    ToSnapshot(card.Creature.Loyalty, ReleasedGrudge(card.Creature) > 0),
                    card.FearThisWave,
                    card.BenefitThisWave,
                    card.GrudgeThisWave,
                    card.RaidersDowned,
                    card.DominantAxis,
                    card.Notability,
                    card.Verdict is { } verdict ? ToVerdictJson(verdict) : null)),
            ]);
    }

    internal static string ToVerdictJson(VerdictKind verdict) => verdict switch
    {
        VerdictKind.Reward => "reward",
        VerdictKind.Punish => "punish",
        _ => throw new InvalidDataException($"Unknown verdict: {verdict}"),
    };
}
