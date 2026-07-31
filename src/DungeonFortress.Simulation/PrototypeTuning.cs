namespace DungeonFortress.Simulation;

public static class PrototypeTuning
{
    // The session no longer ends here: a party ends when the last wave is
    // resolved or when nobody is left standing. This is the fuse that keeps a
    // pathological run finite, so it sits well above the last wave.
    public const int SessionTicks = 2_700;
    public const int ThreatAnnounceTick = 300;

    // The first wave keeps a long runway so the player can learn the levers
    // before anything is at stake; every later wave is announced on the short
    // lead, which is what turns the session into a rhythm instead of one event.
    public const int FirstRaidTick = 1_300;
    public const int WaveCount = 4;
    public const int WaveIntervalTicks = 350;
    public const int WaveAnnounceLead = 200;

    // Composition of a wave is derived from renown, never from the wave number.
    // The base is what an unknown domain attracts; every 20 points of renown
    // buys the raiders one more body, and every 60 one more point of might.
    public const int WaveBaseRaiders = 4;
    public const int WaveMaxRaiders = 12;
    public const int RenownPerExtraRaider = 20;
    public const int RenownPerRaiderMight = 60;

    // Renown never decreases: every term below is itself a monotone counter, so
    // losing creatures, stock or buildings can never buy a better score. That is
    // the guard against the degeneracy that made `overrun` the best outcome.
    //
    // RenownPerWaveArrived equals RenownPerExtraRaider on purpose: having been
    // reached by a wave at all is worth exactly one more raider next time, so
    // every wave is strictly stronger than the one before it whatever the player
    // did. Everything else is weighted well above it, so the score still
    // separates a domain that dug, built, cooked and fought from one that sat
    // still, by roughly two to one over a full party.
    public const int RenownPerWaveArrived = 20;
    public const int RenownPerRaiderDowned = 5;
    public const int RenownPerExcavation = 3;
    public const int RenownPerConstruction = 10;

    // Stock counts through the high-water mark of the larder, divided down so a
    // fat pantry is noticed without drowning out everything else the domain did.
    public const int RenownMealsPerPoint = 2;

    // The party score (ADR 0016). The outcome sets the band and everything else
    // moves the score inside it: surviving the party is worth more than any
    // single thing that can be preserved or lost inside one, which is what keeps
    // a domain that lived above a domain that died. The measured worst raid of
    // the sweep — 66 portions carried off and 35 defenders lost — costs 610 and
    // still leaves the survivor ahead of the fallen.
    //
    // Holding is worth twice surviving, and that band is deliberately unreachable
    // today: no fixture has ever repelled all four waves, and the score is what
    // will show it when one does.
    public const int ScoreOutcomeHeld = 2_000;
    public const int ScoreOutcomeRaided = 1_000;
    public const int ScoreOutcomeFallen = 0;

    // What the party preserved. A wave turned back weighs six times a creature
    // still standing because a wave is the unit the party is made of; a portion
    // left in the larder is the smallest thing worth noticing.
    public const int ScorePerWaveRepelled = 60;
    public const int ScorePerSurvivor = 10;
    public const int ScorePerMealKept = 2;

    // What it lost. A stolen portion costs more than a kept one earns: it is not
    // only gone, its going is what made the raid worth making. A defender put
    // down or broken by morale costs more still — people are harder to replace
    // than supper, and in this slice they cannot be replaced at all.
    public const int ScorePerMealStolen = 5;
    public const int ScorePerDefenderLost = 8;

    // Domain strength: the mirror number. Inborn might weighs more per point
    // than trained form, because might is 1..5 and martialForm is 0..100. What
    // each creature brings is then scaled by its readiness, so the mirror shows
    // condition and not potential — a domain dying of hunger must not be able to
    // report the best strength of its party.
    public const int StrengthPerMight = 2;
    public const int StrengthMartialDivisor = 10;
    public const int StrengthReadinessScale = 100;

    // Healing between waves. A light wound closes only while its owner lies in a
    // bunk and is fed; the domain pays for it in labour, which is what makes the
    // gap between two waves a decision instead of dead time.
    public const int RecoveryMinSatiety = 30;
    public const int HpRecoveryPeriod = 6;

    public const int RaiderHp = 30;
    public const int RaiderMightBase = 3;
    public const int RaiderMightJitter = 1;
    public const int RaiderEntryInterval = 2;
    public const int StealPeriod = 6;
    public const int DefenderHpBase = 20;
    public const int DefenderHpPerMight = 4;
    public const int CombatMinSatiety = 20;
    public const int CombatJoinRecheck = 20;
    public const int EngageRadius = 8;

    // Reach is a property of the attack, not a constant of combat resolution.
    // Everything today is a brawler at one tile; raising this number is the only
    // edit a bow would need on this side of the seam.
    public const int MeleeAttackRange = 1;
    public const int RaiderAttackRange = 1;
    public const int DamageFloor = 1;
    public const int DamageReadinessDivisor = 25;
    public const int ArmourReadinessDivisor = 50;
    public const int DamageJitter = 1;
    public const int LightInjuryShare = 40;
    // Nerve is measured per creature and dread is measured from where that
    // creature is standing. The two new terms are what keep the moment of
    // breaking personal: `MoraleGritWeight` and `MoraleReadinessDivisor` barely
    // move during a fight, while a defender's own wounds and the crowd on top of
    // it change from tick to tick and differently for each of them. A single
    // domain-wide counter against a single threshold broke everyone who happened
    // to sit in the same band on the same tick, which is what Issue #101 saw.
    //
    // Three of the weights were then re-measured on the seed matrix, because
    // asking the question every tick instead of once per casualty changes what
    // each of them is worth (tuning by ADR 0010; the numbers and the runs behind
    // them are in the pull request of #101):
    //
    // - `MoralePerDowned` 10 → 14. The count it multiplies changed meaning. It
    //   used to be every ally the domain had lost anywhere, which on a nine-strong
    //   domain runs 0..8; it is now the allies down inside
    //   `MoraleWitnessRadius`, which runs 0..2. A local count needs a heavier
    //   weight to say the same thing about the same fight.
    // - `MoraleHealthWeight` 24 → 40. Own wounds are the largest thing that
    //   differs between two defenders standing in the same fight, so this term is
    //   what decides who leaves and who stays. At 24 nobody could hold: the whole
    //   line ran and `defendersDowned` fell to 0..1 a party, which quietly
    //   retired injuries, recovery and the cost of a lost wave. At 40 a defender
    //   at full health holds the line and a hurt one does not.
    // - `MoralePerRaiderNear` 7 → 5. Being crowded pushes, but less than watching
    //   somebody drop. At 7 a defender with two raiders in reach broke before a
    //   single ally had fallen, which turned wave after wave into `overrun` —
    //   nobody stayed long enough to put a raider down.
    //
    // `MoraleWitnessRadius` is what a defender can take in from where it stands;
    // `MoralePressRadius` is `RaiderAttackRange` plus one — the raiders that can
    // hit it this tick or the next.
    public const int MoraleGritWeight = 12;
    public const int MoraleReadinessDivisor = 2;
    public const int MoraleBase = 50;
    public const int MoralePerDowned = 14;
    public const int MoraleHealthWeight = 40;
    public const int MoralePerRaiderNear = 5;
    public const int MoraleWitnessRadius = 6;
    public const int MoralePressRadius = 2;
    public const ulong DefaultSeed = 20_260_726UL;

    public const int MapWidth = 28;
    public const int MapHeight = 16;
    public const int MaximumTilesPerCommand = 256;
    public const int MaximumDigDesignations = 256;
    public const int MaximumBuildDesignations = 256;

    public const int StartSatiety = 70;
    public const int StartFatigue = 10;
    public const int StartJitter = 5;
    public const int StartMeals = 8;

    public const int BedGrowthTicks = 45;
    public const int BedRipenessOffset = 5;
    public const int HarvestTicks = 12;
    public const int HarvestOutput = 3;
    public const int CookTicks = 24;
    public const int CookInput = 3;
    // A party is now four waves long instead of one raid, and raiders carry
    // portions out of the larder every time. Two portions a batch fed a single
    // 1800-tick session and starves a four-wave one, so the batch is three.
    public const int CookOutput = 3;
    public const int EatTicks = 8;
    // The larder has two lanes and the domain has nine mouths, so every trip to
    // it is queued as well as walked. Over a four-wave party that queue was
    // costing more labour than the work it interrupted, which is why one portion
    // now carries a creature further.
    public const int MealSatiety = 62;
    public const int CarryCapacity = 3;
    public const int HaulTransferTicks = 2;
    public const int LarderCapacity = 90;
    // A wave of raiders can carry three portions each out of the larder, so a
    // stock that stops at eighteen is emptied by one visit. The domain has to be
    // able to build a buffer that survives being raided — and a fat larder is
    // also what makes it more famous, which is the trade this number sets.
    public const int MealTarget = 30;
    public const int RawTarget = 30;

    public const int SatietyDecayPeriod = 5;
    public const int FatigueGainPeriod = 10;
    public const int RestRecoveryPeriod = 4;
    public const int EatThreshold = 30;
    public const int RestSeekThreshold = 50;
    public const int RestThreshold = 75;
    public const int RestTarget = 20;
    public const int CollapseThreshold = 10;
    public const int ExhaustedSpeedMultiplier = 2;
    public const int AffinitySpeedDenominator = 4;

    // Digging is deliberately slower than a harvest and faster than a cook batch:
    // the player must be able to watch a single tile finish inside one 4x pass.
    public const int DigTicks = 36;
    public const int DigStoneYield = 1;

    // A stockpile cell is deliberately small. The player must be able to fill one
    // inside a five-minute session and see the "no free capacity" wait, instead of
    // painting two cells once and never meeting the constraint again.
    public const int StockpileCellCapacity = 2;
    public const int StoneCarryCapacity = 2;

    // One training post costs exactly one carrier trip. Anything larger would make
    // the first functional room a logistics exercise instead of a demonstration
    // that the excavated stone finally turns into a working object.
    public const int BuildStoneCost = 2;
    public const int BuildTicks = 30;

    public const int DrillTicks = 30;
    public const int DrillGain = 12;
    public const int DrillFatigue = 6;
    public const int DrillSatietyCost = 3;
    public const int WatchSlots = 2;
    public const int WatchFatiguePeriod = 20;
    public const int RationSatietyGate = 40;

    public const int ScorePriorityWeight = 100;
    public const int ScoreAffinityWeight = 30;
    public const int UrgencyLowMeals = 60;
    public const int LowMealsThreshold = 4;
    public const int UrgencyHaulMeal = 40;
    public const int UrgencyHaulRaw = 20;
    // Zero on purpose: stone shares the global Haul priority with food, so the
    // food chain must keep winning an otherwise equal comparison.
    public const int UrgencyHaulStone = 0;
    public const int UrgencyRipeBacklog = 20;
    public const int RipeBacklogThreshold = 3;
    public const int ScoreFloor = 0;

    public const int ReadinessBase = 10;
    public const int ReadinessSatietyNumerator = 1;
    public const int ReadinessSatietyDenominator = 2;
    public const int ReadinessMartialNumerator = 3;
    public const int ReadinessMartialDenominator = 10;
    public const int ReadinessRestDenominator = 10;
    public const int InjuryLightPenalty = 15;
    public const int InjuryHeavyPenalty = 40;

    public const int PriorityMinimum = 0;
    public const int PriorityMaximum = 4;
    public const int DefaultHarvestPriority = 3;
    public const int DefaultHaulPriority = 3;
    public const int DefaultCookPriority = 3;
    public const int DefaultRestPriority = 2;
    public const int DefaultDrillPriority = 0;
    public const int DefaultWatchPriority = 0;
    // Digging must start from a designation alone, so its default priority is
    // active. It ties with the food chain and loses the tie by enum order, which
    // keeps the existing food/raid vertical unchanged until rock is designated.
    public const int DefaultDigPriority = 3;
    // Same reasoning as Dig: a blueprint must lead to work on its own. Build is
    // last in enum order, so an equal score still loses to the food chain and to
    // excavation, and the default priority changes nothing until a blueprint exists.
    public const int DefaultBuildPriority = 3;
    public const int RationReserveMaximum = 20;
    public const int RationReserveDefault = 0;
    public const int DrillMinimumSatietyMaximum = 100;
    public const int DrillMinimumSatietyDefault = 40;
    public const int MusterLeadMaximum = 300;
    public const int MusterLeadDefault = 0;
}
