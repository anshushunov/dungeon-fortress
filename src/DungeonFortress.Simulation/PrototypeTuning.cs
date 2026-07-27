namespace DungeonFortress.Simulation;

public static class PrototypeTuning
{
    public const int SessionTicks = 1_800;
    public const int ThreatAnnounceTick = 300;
    public const int RaidTick = 1_500;
    public const int RaiderCount = 4;
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
    public const int DamageFloor = 1;
    public const int DamageReadinessDivisor = 25;
    public const int ArmourReadinessDivisor = 50;
    public const int DamageJitter = 1;
    public const int LightInjuryShare = 40;
    public const int MoraleGritWeight = 12;
    public const int MoraleReadinessDivisor = 2;
    public const int MoraleBase = 50;
    public const int MoralePerDowned = 10;
    public const ulong DefaultSeed = 20_260_726UL;

    public const int MapWidth = 28;
    public const int MapHeight = 16;
    public const int MaximumTilesPerCommand = 256;
    public const int MaximumDigDesignations = 256;

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
    public const int CookOutput = 2;
    public const int EatTicks = 8;
    public const int MealSatiety = 46;
    public const int CarryCapacity = 3;
    public const int HaulTransferTicks = 2;
    public const int LarderCapacity = 90;
    public const int MealTarget = 18;
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
    public const int RationReserveMaximum = 20;
    public const int RationReserveDefault = 0;
    public const int DrillMinimumSatietyMaximum = 100;
    public const int DrillMinimumSatietyDefault = 40;
    public const int MusterLeadMaximum = 300;
    public const int MusterLeadDefault = 0;
}
