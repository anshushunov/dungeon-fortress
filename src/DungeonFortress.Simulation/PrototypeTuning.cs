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
    // How much health one period of mending returns. It used to be the literal
    // 1 in MendTheWounded, and that 1 was denominated in the old health units:
    // with health eight times larger it would have made a wound take eight times
    // as long to close, which is a change nobody decided. Eight per six ticks on
    // a scale eight times larger is the same fraction of a creature per tick as
    // one per six ticks was — so the window between two waves buys exactly what
    // it bought before, and the price of a lost wave is still measured in labour
    // rather than in a longer wait.
    //
    // **It is load-bearing, and it bears more than the mending ladder.** Renown
    // counts raiders put down, and whether a domain puts them down is decided by
    // whether it entered the wave mended. Dropping this number back to the old
    // literal 1 reddens sixteen checks of the package, and among them is
    // `Deliberately_losing_creatures_and_stock_never_scores_better` — the promise
    // that impoverishment must not pay — at 148 for giving up half way against
    // 113 for keeping. That check is held by THIS number and not by
    // DamageReadinessDivisor, which was measured and does not hold it. Mutant M13
    // and the search for it are in evidence/333-mutants.json.
    public const int HpRecoveryStep = 8;

    // The combat economy is denominated in units eight times the old ones on the
    // health side and four times the old ones on the damage side (Issue #336,
    // owner's decision of 2026-08-08: «по дефолту хп всех добавить и сделать
    // дамаг рандом какой-то, чтобы чуть дольше было и менее детерминированно»).
    //
    // Two different multipliers on purpose, and the difference between them is
    // the whole of «дольше»: a blow buys half of what it used to buy, so an
    // exchange takes about twice as many of them. Measured on the nine shipped
    // parties, pooled over every body that actually fell — seven blows to fell a
    // raider before, fourteen after (evidence/333-before-merged.json and
    // evidence/333-after-merged.json).
    //
    // The damage side is not merely multiplied, it is given resolution, and that
    // is the second half of the change rather than a side effect. A blow used to
    // be `might + readiness/25`, and readiness — half of which is satiety —
    // therefore entered it as one of four whole numbers: a creature at satiety 20
    // and a creature at satiety 40 struck exactly as hard. In the new
    // denomination readiness enters as `readiness/6`, so it is worth up to 16
    // points of a blow of about 20 and every four points of satiety are visible
    // in it. That is what carries the two properties this slice had to restore by
    // design instead of by accident — see PrototypeWorld.Combat.cs, ActCombatant.
    public const int RaiderHp = 240;
    public const int RaiderMightBase = 3;
    public const int RaiderMightJitter = 1;
    public const int RaiderEntryInterval = 2;
    public const int StealPeriod = 6;
    public const int DefenderHpBase = 160;
    public const int DefenderHpPerMight = 32;
    // Getting into the line is harder than staying in it, and the two numbers say
    // so separately (Issue #333, owner's decision of 2026-08-11).
    //
    // Before them there was one number, 20, and it was only ever asked on the way
    // in. What took a hungry fighter back out was not a rule of combat at all: it
    // was the needs phase overwriting CreatureMode two phases later, at
    // EatThreshold (30), with nothing in the journal saying the line had lost
    // anybody and nothing in the wave counting it. Removing that overwrite without
    // replacing it would have meant hunger could no longer end anybody's fight,
    // and the invariant that impoverishment must never pay broke on exactly that:
    // a party that stopped feeding at t1400 scored 138 against 133 for one that
    // kept feeding (evidence/333-variants.json).
    //
    // CombatJoinSatiety is 30. It was 41 for one round, chosen on the balance
    // surface the longer fight of Issue #336 creates, and the paragraphs below
    // are the record of that choice; the value moved to 30 on the owner's
    // constraint «not above EatThreshold», because above the eat threshold the
    // domain cannot sustain fitness for a fight at all and the HUD reads
    // `strength 0` for nine healthy creatures. The sweep and the rule of choice
    // are evidence/333-tension.json, section
    // theLowThresholdSweepUnderTheEatThresholdConstraint; 30 is the largest of
    // the admissible values and the only one on which the label invariant of
    // Issue #389 holds. Read the paragraphs below as history of the 41, not as a
    // description of the constant.
    //
    // 41 rather than 42, and the difference is one measured defect deep. The value
    // was 42 while the check `A_verdict_makes_the_named_creature_behave_differently
    // _in_the_next_wave` could only ever look at `baseline`: it built its arms by
    // REPLACING the fixture's command log rather than adding to it, so a fixture
    // that carries commands could not be asked at all. With that repaired the
    // check sees the whole shipped matrix, and the matrix says the scene it looks
    // for leaves `baseline` and arrives in `prepared` as the fight lengthens — so
    // what read as a balance conflict was in part an instrument that could not
    // turn its head. Repaired, the sweep of 40..42 has both of the opposed checks
    // green at 40 and at 41, and neither at 42.
    //
    // 40 is excluded and 41 taken: at 40 the whole suite gives seven red, among
    // them `The_contract_invariants_hold_on_every_seed_of_the_matrix` and
    // `Preparation_changes_the_deterministic_party_without_direct_orders` — one of
    // the two properties this slice exists to restore — while at 41 it gives
    // three. 41 also carries the observability floor of memory of place with room
    // (51 refusals on `prepared` against a floor of 10) where 40 meets it exactly.
    // The sweep and the rule of choice stated before it are in
    // evidence/333-tension.json; what moves with the threshold is in
    // evidence/333-after-merged.json, section `joinThresholdOnTheLongFight`.
    //
    // There is ONE threshold and it is asked on the way in only. A second one,
    // CombatHoldSatiety = 20, was introduced by the owner's decision of
    // 2026-08-11 to let hunger take a fighter out of the line through combat's
    // own door, and it was REMOVED by the owner's decision of the same day once
    // it was measured: no party can reach it. A creature is in the line only if
    // it entered above the join threshold; while fighting its satiety only falls,
    // and only by the global decay of one point per SatietyDecayPeriod ticks; and
    // a spell in the line ends when the wave resolves. So the fall from join to
    // below hold cost (join - hold + 1) * 5 unbroken ticks in the line — 110 at a
    // join of 41 — against a longest spell of 53 ticks measured over twenty-four
    // party-runs at four thresholds, and nothing in the command vocabulary
    // lengthens a wave. The measurement is evidence/333-hold-reachability.json.
    // The precedent for deleting rather than annotating is this same method:
    // independent review of PR #328 removed an unreachable branch from it, on the
    // grounds that a clause the mechanics cannot reach is a promise the contract
    // does not keep.
    //
    // AND THAT ARITHMETIC WAS TAKEN AT A JOIN OF 41. At 30 it gives a different
    // answer, so it is restated here rather than left to be rediscovered: the
    // fall from 30 to 19 is eleven points at five ticks each, which is 55 unbroken
    // ticks and not 110; the longest unbroken spell in the line measured on the
    // final party is 69 ticks (prepared/20260726); and the lowest satiety observed
    // on a creature while it was fighting is 21, one point above the hold
    // threshold that was removed. So the ground on which the rule was retired as
    // unreachable does not reproduce at this threshold. The rule is NOT brought
    // back by this slice — it was removed by the owner's decision and combat
    // mechanics are his call, not an executor's — and the fact is recorded so that
    // the next decision is taken on the numbers of the party that exists.
    // Measurement: evidence/333-starving-reachability.json,
    // theHoldRuleArithmeticAtThirty.
    //
    // What this leaves standing: a fighter is never taken out of the line by
    // hunger at all. It falls, or it breaks, or the wave ends. The promise that a
    // hungry domain fights worse therefore rests ENTIRELY on
    // DamageReadinessDivisor below — see the note there before touching it.
    public const int CombatJoinSatiety = 30;
    public const int CombatJoinRecheck = 20;
    public const int EngageRadius = 8;

    // Reach is a property of the attack, not a constant of combat resolution.
    // Everything today is a brawler at one tile; raising this number is the only
    // edit a bow would need on this side of the seam.
    public const int MeleeAttackRange = 1;
    public const int RaiderAttackRange = 1;
    // Floored at one and not at the new denomination's four. It is the reading
    // «an attack in reach always lands», which BlowReadoutTests holds by name,
    // and raising it would quietly turn the armour term into a number that can
    // never win.
    public const int DamageFloor = 1;
    // What a defender's own condition is worth in its blow, and what it is worth
    // in the armour that meets a raider's. Both were divided by 25 and 50 and are
    // now divided by 6 and 12 — the same ratio between them, four times the
    // resolution, because the numbers they divide into are four times larger.
    //
    // **DamageReadinessDivisor is load-bearing, and it is the only thing bearing
    // that load.** Readiness is half satiety, so this divisor is the whole of the
    // rule «a hungry domain fights worse». It used to share the job with a second
    // satiety threshold that pulled a starving fighter out of the line; that rule
    // was removed on 2026-08-11 as unreachable (see CombatJoinSatiety above), so
    // hunger now has exactly one way to reach a fight: through this number. At 25
    // the whole range of readiness was worth four integers of damage and a
    // fighter at satiety 20 hit exactly as hard as one at 40 — which is how the
    // property came to be riding on a defect in the first place. At 6 every four
    // points of satiety are visible in a blow, and a fight of about thirteen
    // blows is long enough for the difference to decide whether a raider falls.
    // Anyone raising this divisor is spending that property, and exactly ONE
    // check goes red when they do: `Preparation_changes_the_deterministic_party_
    // without_direct_orders`. It is named here so that a change to the number
    // meets the consequence rather than discovering it.
    //
    // This note used to name a second one,
    // `Deliberately_losing_creatures_and_stock_never_scores_better`, and that was
    // a promise the party does not keep. Measured by the mutants of this slice
    // (evidence/333-mutants.json, M3): at a divisor of 25 it stays green, and it
    // stays green at 1000 too — that is, with readiness contributing nothing to a
    // blow at all. The name was removed rather than left standing, for the same
    // reason round 3 of this slice removed a false promise from the neighbouring
    // constant: a note that names a check which cannot fail is worse than a note
    // that names none, because it is read as coverage.
    //
    // What DOES hold that check is HpRecoveryStep, and it was found by putting a
    // mutant on each candidate rather than by picking one
    // (evidence/333-mutants.json, theSearchForWhatHoldsDeliberatelyLosing).
    // Renown counts raiders put down, the whole gap between the two runs of that
    // check sits in that single term — 16 against 10 — and what decides it is
    // whether the domain enters the third wave mended. Of the seven substitutions
    // tried, six leave the check green, this divisor at 1000 and the join
    // threshold at 0 among them; the one that reddens it is
    // `HpRecoveryStep = 8 -> 1`, at which giving up half way scores 148 against
    // 113 for keeping (M13). So the two properties of the slice rest on two
    // different numbers, and this constant bears exactly one of them:
    // `Preparation_changes_the_deterministic_party_without_direct_orders`.
    public const int DamageMightWeight = 4;
    public const int DamageReadinessDivisor = 6;
    public const int RaiderMightWeight = 5;
    public const int ArmourReadinessDivisor = 12;
    // How far a blow scatters, plus or minus, drawn per blow from the party's own
    // DeterministicRandom stream (PrototypeWorld.CombatJitter). Six on a blow of
    // about twenty is a third either way, against the ±1 on about 4 — a quarter —
    // that the old denomination had.
    //
    // The shape stays uniform on [−a, +a] and the alternative was measured rather
    // than dismissed: a triangular roll, the sum of two half-width uniforms, was
    // tried at the same total width and rejected. It is the same mean and a
    // narrower middle, so it makes the common exchange *more* predictable and
    // spends the amplitude on tails a party of four waves sees a handful of
    // times — the opposite of what the owner asked for. The numbers are in
    // evidence/333-after-merged.json, section `theShapeOfTheSpread`.
    public const int DamageJitter = 6;
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
    // - `MoralePerDowned` 10 → 14, the only one of the three that existed before.
    //   The count it multiplies changed meaning: it used to be every ally the
    //   domain had lost anywhere, which on a nine-strong domain runs 0..8, and it
    //   is now the allies down inside `MoraleWitnessRadius`, which runs 0..2. A
    //   local count needs a heavier weight to say the same thing about the same
    //   fight.
    // - `MoraleHealthWeight`, new, settled at 40. Own wounds are the largest thing
    //   that differs between two defenders standing in the same fight, so this
    //   term is what decides who leaves and who stays. Tried at 24 first, and at
    //   24 nobody could hold: the whole line ran and `defendersDowned` fell to
    //   0..1 a party, which quietly retired injuries, recovery and the cost of a
    //   lost wave. At 40 a defender at full health holds and a hurt one does not.
    // - `MoralePerRaiderNear`, new, settled at 5. Being crowded pushes, but less
    //   than watching somebody drop. Tried at 7 first, and at 7 a defender with
    //   two raiders in reach broke before a single ally had fallen, which turned
    //   wave after wave into `overrun` — nobody stayed long enough to put a
    //   raider down.
    //
    // The 24 and the 7 above are candidates weighed against 40 and 5 inside this
    // change set, not values `main` ever ran: before #101 neither weight existed.
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
    // Memory of place (Issue #117). A creature remembers where its nerve failed
    // and where a raider put it down, and will not start work there.
    //
    // Three places, because a party is four waves long and a creature that
    // remembered every one of them would end it unable to work anywhere the
    // fighting reached. When a fourth arrives the oldest goes: what a creature
    // avoids is what happened to it recently, not the whole party.
    //
    // Radius four, measured in tiles walked, and it is a measurement rather than
    // a taste. Two was tried first, on the argument that a creature should refuse
    // the part of a room where it happened rather than the room — and at two the
    // refusal **never fired at all**, over the whole seed matrix and both
    // fixtures that reach a wave. The reason is geometry the argument did not
    // know about: the raiders' road runs three to four tiles east of the larder,
    // so that is where defenders meet them and where memories are written, and
    // there is no work on it to refuse. Four is the first radius that reaches
    // back to the larder tiles themselves, and at four the refusal fires 9 to 69
    // times a party.
    //
    // What two was right about is a real cost of four: at four a creature
    // standing three tiles from a remembered place is inside the refusal, so
    // "the part of the room" is closer to "the room". That is the price of the
    // mechanic being observable at all on this map, and it is named rather than
    // hidden.
    //
    // Issue #171: the measurement above was taken before Issue #129, and the map
    // it describes is not the map any more. The approach rule puts defenders on
    // the tiles around a raider instead of behind one another, a raider stands at
    // the larder, and so memories are now written on the larder itself — four of
    // the sixteen on baseline/20260728, with four more within two tiles of it
    // (evidence/171-before.json). "There is no work on the road to refuse" was
    // the whole reason four was needed, and it has stopped being true.
    //
    // At four the reach is a diamond of 41 tiles. From (14,7) it covers every
    // larder tile and all of the kitchen but its west column, so one broken nerve
    // on one larder tile takes the whole food chain away from that creature —
    // measured as 301 refusals of a haul at (11,7), three steps away, by a
    // creature that remembers (14,7).
    //
    // And four stays, because shrinking it was tried against the whole matrix and
    // it buys the wrong thing. At three the domain stops falling but still ends
    // baseline/20260728 at satiety 8 with 459 refusals; at two it ends at 30, and
    // `prepared` — the fixture where memories land near the gate and the only work
    // in reach is the watch post — falls from 40 refusals over its three seeds to
    // 5, below the floor `PrototypeMemoryTests` holds the slice to. Reach is what
    // makes the mechanic observable on `prepared` at all, so spending it to fix
    // `baseline` would pay for the price of memory with the evidence that memory
    // exists. The two bounds below are the ones that cost nothing of that: they
    // are about how long the price lasts and how much of it the domain can be made
    // to pay, not about how far it reaches. Every figure in this paragraph is in
    // evidence/171-after.json, section `sweep`.
    public const int MemoryPlacesMax = 3;
    public const int MemoryAvoidRadius = 4;

    // How long one memory goes on refusing work, in ticks since it was written.
    // The second half of the price of Issue #171, and the one that had no value
    // at all before it: a remembered place refused work for the rest of the
    // party, and the only thing that could ever stop it was being pushed out by
    // MemoryPlacesMax.
    //
    // The place itself is not forgotten when this runs out. What ages is the
    // avoidance, not the memory: the creature still carries the tile, the panel
    // still lists it and the player can still read what happened there. That is
    // the distinction ADR 0018 asks each following slice to make when it says
    // memory "живёт дольше одного тика и одной волны, поэтому каждый следующий
    // слайс обязан объяснять, что с ней происходит".
    //
    // Two hundred, and the number has a shape rather than a taste: it is shorter
    // than WaveIntervalTicks = 350, so a fright never outlives the quiet window
    // the party is supposed to use to feed itself, and by the time the next wave
    // arrives the creature is working there again. Longer values were measured on
    // the matrix and leave the cost in place — at 300, baseline/20260728 still
    // ends at satiety 22 with 241 refusals against 39 and 122 here.
    public const int MemoryAvoidTicks = 200;

    // The satiety at which memory stops refusing work at all. Below it a creature
    // is too hungry to be choosy and takes the job it would otherwise have walked
    // away from.
    //
    // This is the other half of the price of Issue #171 and the one that bounds
    // it rather than shortening it: whatever memory costs the domain, it may not
    // cost it its life. Without this bound the two dimensions above multiply into
    // a party that wins every fight and starves, because the tiles a nerve breaks
    // on after Issue #129 are the tiles the food chain runs through, and a
    // creature standing still with an empty larder in front of it is not making a
    // different choice.
    //
    // Thirty is EatThreshold: exactly the point at which the same creature already
    // drops everything to go and eat. The rule therefore adds no new moment to the
    // simulation — it says that at the moment hunger already overrides what a
    // creature was doing, it overrides what it was refusing as well. Twenty was
    // tried and is too late: baseline/20260728 survives it but ends at satiety 13,
    // because a creature that only yields at 20 yields after the larder is already
    // empty.
    //
    // What it deliberately does not do is make the refusal untrue. A creature that
    // yields records no refusal, so nothing in the log claims a decision that did
    // not happen; the truthfulness rule of Issue #125 is untouched. And it is not
    // a rule about the map: it never asks what stands on the tile, only what the
    // creature has left.
    public const int MemoryYieldsSatiety = 30;

    // ------------------------------------------------------------------
    // Loyalty and the moment of truth (slice 3 of the pitch's order of proof).
    // Design contract: docs/design/SLICE_03_MOMENT_OF_TRUTH.md. Everything here
    // is tuning by ADR 0010; what is an invariant is stated in that document and
    // not in these numbers.
    // ------------------------------------------------------------------

    // Fear. Being put down is the worst thing that can happen to a creature in
    // this prototype and is weighted accordingly; a single blow barely registers
    // on its own but accumulates over a long fight. Watching an ally fall is
    // worth less than falling, and more than being hit.
    public const int LoyaltyFearWound = 8;
    public const int LoyaltyFearPanic = 5;
    public const int LoyaltyFearAllyDowned = 3;

    // Quiet ticks that buy back one point of fear. Sixty is deliberately shorter
    // than WaveIntervalTicks = 350, so a domain that leaves its people alone
    // between two waves gets a measurable part of the fright back — which is
    // what lets a grudge surface at all (pitch 6.3).
    public const int LoyaltyFearFadePeriod = 60;

    // Benefit. A portion is the smallest ordinary kindness and the most frequent
    // one; a mended wound is the largest, because the domain spent a bunk and a
    // ration on it while a wave was coming.
    public const int LoyaltyBenefitFed = 2;
    public const int LoyaltyBenefitTended = 4;
    public const int LoyaltyBenefitFadePeriod = 60;

    // Grudge as the delayed price of fear. Nothing is credited below this floor,
    // so a domain nobody is afraid of accumulates no resentment at all. How much
    // of what is accumulated is *acted on* is not a second constant: it is
    // whatever the fear no longer covers (ReleasedGrudge), which is the "пока
    // страх высок, обида не видна" half of the same sentence of pitch 6.3.
    public const int LoyaltyGrudgeFearFloor = 5;

    // Ticks of one coercion that buy one point of resentment. Both are short
    // relative to a wave interval and long relative to a decision, so a passing
    // hunger costs nothing and a domain that keeps somebody hungry pays.
    public const int LoyaltyGrudgeHungerPeriod = 100;
    public const int LoyaltyGrudgeHunger = 1;
    public const int LoyaltyGrudgeRefusedPlacePeriod = 100;
    public const int LoyaltyGrudgeRefusedPlace = 1;

    // How much resentment is spent when it is finally acted on. Less than a
    // punishment costs, so one refusal does not clear the whole account.
    public const int LoyaltyGrudgeDischarge = 6;

    // What a verdict is worth. A punishment frightens more than a single wound
    // and, when it lands on somebody who did nothing wrong, buys the domain an
    // equal amount of resentment it will pay for later. Ignoring a creature the
    // domain itself put on a card costs less than punishing it and is not free —
    // ADR 0019 requires the absence of a verdict to have a consequence.
    public const int LoyaltyVerdictRewardBenefit = 12;
    public const int LoyaltyVerdictPunishFear = 10;
    public const int LoyaltyVerdictPunishUnfairGrudge = 14;
    public const int LoyaltyGrudgeIgnored = 2;

    // How loyalty moves the choice of work. The cap is below one step of
    // affinity (30) and far below one step of priority (100), so loyalty can
    // decide between two comparable jobs and can never override what the player
    // asked the domain to care about. That bound is the executable half of "ни
    // одно значение не делает ни одно поведение неизбежным" (Issue #167).
    public const int LoyaltyWorkGrudgeDivisor = 3;

    // How much fear buys one point of readiness to take the work on offer. It is
    // the "работают голодными … терпят несправедливость" half of pitch 6.3, and
    // it is the only reading fear has in ordinary life: nerve is deliberately
    // closed to it, because there fear of the domain and fear of the fight would
    // be added together and the second one means the opposite.
    public const int LoyaltyWorkFearDivisor = 8;

    // How much benefit buys one tile of forgiven distance, and how many tiles it
    // may forgive at most. Six, because the upkeep a domain owes anyway — a
    // portion now and then — must not buy anything: benefit from feeding alone
    // hovers at nought to four over a party (evidence/312-loyalty-ledger.json),
    // so an ordinary well-fed creature walks exactly as far as it did before this
    // mechanic existed, and it is a verdict (twelve at a stroke) or a domain that
    // trained somebody all party that moves it. Four tiles is the cap: it is the
    // width of a room on this map, so a reward can take a creature into the next
    // room and never across the dungeon.
    public const int LoyaltyWorkReachDivisor = 3;
    public const int LoyaltyWorkReachCap = 6;
    public const int LoyaltyWorkBiasCap = 20;

    // Whether resentment outweighs everything that holds a creature in the line
    // when the domain calls it to a fight. A contest and not a gate: a single
    // verdict can neither cause nor prevent the refusal on its own.
    public const int LoyaltyRefuseGrudgeWeight = 3;
    public const int LoyaltyRefuseGritWeight = 4;

    // The moment of truth. Three cards, because the pitch says three to five and
    // three is the smallest number at which the player is choosing rather than
    // acknowledging. The window is measured in steps of the runner and not in
    // ticks of the world, because the world does not tick while it is open: it
    // is how long the domain waits for an answer before deciding it will not get
    // one. Forty steps is long enough for a player to read three cards and short
    // enough that a headless fixture with no verdicts in it is not slowed to a
    // crawl.
    public const int MomentOfTruthCards = 3;

    // What one raider put down is worth when the domain decides whom to report
    // on. It sits above anything a single wave can move a magnitude by, so a
    // creature that did something the player might want to answer for is always
    // on a card, and the cards about standing alone fill what is left.
    public const int MomentOfTruthDeedWeight = 50;
    public const int MomentOfTruthWindowSteps = 40;

    // ------------------------------------------------------------------
    // The returning raider (slice 5 of the pitch's order of proof, section 6.8).
    // Design contract: docs/design/SLICE_05_RETURNING_HERO.md. Tuning by ADR 0010;
    // what is an invariant is stated in that document and not in these numbers.
    // ------------------------------------------------------------------

    // How many waves later a raider who left alive comes back. Two, which is the
    // shortest reading of the pitch's «через несколько волн» that still leaves a
    // wave the domain does not see him in: one would make "he is back" and "he
    // never left" the same observation.
    public const int ReturningRaiderWaveGap = 2;

    // What he brings back, and it is one number rather than two.
    //
    // Might is the whole of the strengthening. It is deliberately the smallest
    // bonus that is a bonus at all — might is an integer and raiderMight runs 3..5
    // over a party — and that is a measurement rather than a taste: at +2 with
    // twenty extra health the domain's score on baseline/20260726 falls from 636
    // to 613 and one more portion goes out of the gate, which turns the return
    // into a second driver of difficulty beside renown. Slice 1 proved that the
    // strength of a wave is what the domain's own visibility bought, and a slice
    // about stories may not quietly add to it.
    //
    // There was a health bonus here and it was removed by measurement rather than
    // by taste. Over eight parties of baseline (seeds 20260726, 20260729,
    // 20260730, 424242, 1, 2, 3, 7) there are 54 returning raiders; ten extra
    // health keeps exactly one of them off the floor and leaves every party's
    // score and every count of stolen portions identical to the tick. A tuning
    // value whose effect cannot be read is a promise the contract cannot keep, so
    // the strengthening is one knob whose effect is named in numbers —
    // evidence/358-strengthening.json.
    public const int ReturningRaiderMightBonus = 1;

    // How many creatures the domain has. It lives here because the static
    // pre-flight of a verdict needs to bound `creatureId` before any world
    // exists, and the population is fixed for the whole of Prototype 1
    // (contract 5.2).
    public const int CreatureCount = 9;

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

    // How long a creature with nothing to do stands where it is before it walks
    // off to the quarters (Issue #201). The delay exists so that the gap between
    // two pieces of work does not send anybody across the dungeon and back: a
    // creature that is idle for a tick or two is between jobs, not off duty.
    //
    // Eight is shorter than the shortest piece of work in the prototype — a
    // harvest is HarvestTicks = 12 — so leaving never competes with work that was
    // about to arrive; and it is long enough that the one-tick gaps the matching
    // produces every tick do not count. It is deliberately not derived from
    // WaveIntervalTicks: this is a rule about a creature's own idleness, not about
    // the rhythm of waves.
    public const int OffDutyDelayTicks = 8;

    // How long a raider waits for an occupied tile to clear before it takes the
    // crowded one anyway (Issue #76, criterion 2: a blocked body must not stall
    // silently, so the wait has a limit).
    //
    // The limit is not a nicety, it is what keeps the party finishing. A wave
    // resolves only when every raider that entered has stopped raiding, and a
    // raider only stops by filling CarryCapacity or by being felled. Measured
    // without a limit: raiders queued behind one another never reached the larder,
    // no wave after the first resolved, and the party ran to SessionTicks with a
    // null outcome - `The party did not end (outcome null)`. Four ticks is long
    // enough for an occupant that is walking through to clear the tile, since a
    // raider moves every tick, and far short of the eighteen a full theft takes.
    public const int RaiderBlockedPatience = 4;

    // Ticks of extra delay per creature id before it leaves the ground a fight
    // was fought on. A group that stands up all on the same tick walks off as a
    // column and reproduces in the corridor exactly the jam it just left.
    //
    // The quantity is the one `Walking_round_reaches_more_of_the_clinch_than_a_
    // yield_could` asserts — detourWithABody / yieldCouldClear, floor 1.5 — and
    // the three readings are: with the stagger 937 / 605 = 1.55; without it
    // 916 / 639 = 1.43, under the floor, and the test goes red; on origin/main,
    // before this rule existed, 639 / 344 = 1.86.
    //
    // Leaving one at a time is what a group does anyway, and by id it stays
    // deterministic.
    public const int OffDutyStaggerTicks = 3;

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
