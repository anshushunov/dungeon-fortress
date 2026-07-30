namespace DungeonFortress.Simulation;

/// <summary>
/// The score of a finished party: what the domain kept, minus what it lost,
/// inside the band its outcome puts it in.
///
/// It is a different number from renown and exists for a different question.
/// Renown answers "how visible am I from outside" every tick of the party and
/// sets the strength of the next wave, so it may never fall — a score that
/// falls would make impoverishment a strategy (ADR 0015). This one answers
/// "did I play well", is read once, at the end, and must fall when the domain
/// loses people or supplies. One number cannot do both, which is what
/// <see href="../../docs/decisions/0016-party-score-separate-from-renown.md">
/// ADR 0016</see> separated.
///
/// Three properties are invariants and the rest is tuning by ADR 0010:
///
/// - the outcome ranks strictly: all else equal, held beats raided beats
///   fallen;
/// - it pays for what survived the party — people still in the line, portions
///   still in the larder, waves turned back — and never for how long the party
///   lasted, because time is not quality. There is deliberately no term for
///   waves that merely arrived and none for the tick the party ended on;
/// - raiders put down are absent on purpose. That term is the noisiest thing
///   the party produces (measured 30/60/45 against 70/45/55 on three seeds),
///   and a ranking built on it ranks combat luck.
///
/// Nothing in the simulation reads this number: like domain strength it is a
/// mirror, and the only value that feeds back into the world is renown.
/// </summary>
public static class PrototypePartyScore
{
    /// <summary>
    /// The score of a party that ended.
    /// </summary>
    /// <param name="outcome">
    /// <c>held</c>, <c>raided</c> or <c>fallen</c>. A party that is still being
    /// played, or one the session fuse cut short, has no score at all rather
    /// than a zero — see <see cref="PrototypeSessionResultSnapshot.Score"/>.
    /// </param>
    /// <param name="wavesRepelled">Waves actually turned back.</param>
    /// <param name="survivors">
    /// Creatures the domain still has in the line: neither on the floor nor
    /// below the collapse threshold. It is the per-creature negation of the
    /// rule that declares a domain fallen (contract 3.2), which is what makes
    /// a fallen domain score zero survivors without a special case.
    /// </param>
    /// <param name="mealsKept">Portions left in the larder at the end.</param>
    /// <param name="mealsStolen">Portions carried out of the gate by raiders.</param>
    /// <param name="defendersLost">
    /// Defenders the domain failed to keep in the line, summed over the waves:
    /// put down or broken by morale.
    /// </param>
    public static int Compute(
        string outcome,
        int wavesRepelled,
        int survivors,
        int mealsKept,
        int mealsStolen,
        int defendersLost)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var band = outcome switch
        {
            "held" => PrototypeTuning.ScoreOutcomeHeld,
            "raided" => PrototypeTuning.ScoreOutcomeRaided,
            "fallen" => PrototypeTuning.ScoreOutcomeFallen,
            // A fourth end of a party is a defect, and guessing a band for it
            // would quietly rank an outcome nobody taught the score about.
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "The party score has no band for this end of a party and will " +
                "not invent one. Teach it the new outcome instead."),
        };

        return band
            + wavesRepelled * PrototypeTuning.ScorePerWaveRepelled
            + survivors * PrototypeTuning.ScorePerSurvivor
            + mealsKept * PrototypeTuning.ScorePerMealKept
            - mealsStolen * PrototypeTuning.ScorePerMealStolen
            - defendersLost * PrototypeTuning.ScorePerDefenderLost;
    }
}
