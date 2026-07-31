using DungeonFortress.Presentation;
using DungeonFortress.Simulation;

using Xunit;
using Xunit.Abstractions;

namespace DungeonFortress.Presentation.Tests;

/// <summary>
/// The event adapter of Issue #117: what the player reads instead of a reason
/// code.
///
/// The boundary this set exists to hold is not "the codes are gone" — they are
/// not, and may not be. The existence of a reason code as the mechanism of
/// explanation is an invariant of
/// <c>docs/decisions/0010-contract-invariants-and-tuning.md</c>; the canonical
/// snapshot and the canonical event log carry it exactly as before, and
/// <see cref="The_canonical_state_still_carries_the_reason_code_the_feed_renders"/>
/// says so executably. What changed is only what is drawn.
/// </summary>
public sealed class EventNarrationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Every reason code the shipped fixtures actually produce has a sentence.
    ///
    /// The guard is the run rather than a list, because a list would be a second
    /// copy of the codes and would go stale in exactly the way a catch-all arm
    /// would hide. A code that reaches the feed without a sentence throws, so
    /// this fails loudly rather than rendering the wrong story.
    /// </summary>
    [Fact]
    public void Every_reason_code_the_matrix_produces_has_a_sentence()
    {
        var covered = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fixtureName in new[] { "baseline", "prepared", "neglected" })
        {
            var state = PresentationFixtures.RunFixture(fixtureName, PrototypeTuning.SessionTicks);
            foreach (var @event in state.Events)
            {
                var sentence = EventNarration.Describe(state, @event);
                Assert.False(string.IsNullOrWhiteSpace(sentence));
                Assert.DoesNotContain(@event.ReasonCode, sentence, StringComparison.Ordinal);
                covered.Add(@event.ReasonCode);
            }

            foreach (var creature in state.Creatures)
            {
                Assert.False(string.IsNullOrWhiteSpace(EventNarration.Describe(state, creature)));
            }
        }

        output.WriteLine(string.Join("\n", covered));
        Assert.True(
            covered.Count >= 20,
            $"only {covered.Count} distinct reason codes were exercised, which is too few for " +
            "this to be a guard over the feed at all.");
    }

    /// <summary>
    /// A code the adapter has never been taught is refused rather than guessed.
    /// This is the assertion that makes the one above worth anything: without it
    /// a catch-all arm would satisfy every check here while telling the player a
    /// sentence about something else.
    /// </summary>
    [Fact]
    public void An_unknown_reason_code_is_refused_rather_than_guessed()
    {
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() =>
            EventNarration.Sentence(
                "chosen_something_nobody_wrote",
                new Dictionary<string, int>(StringComparer.Ordinal),
                JobKind.Haul,
                new GridPoint(1, 1)));
        Assert.Contains("will not invent one", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two sentences memory of place adds, checked on their own rather than
    /// only through a party: they are the ones Issue #117 asks the player to be
    /// able to retell, and they have to name the creature, the place and what
    /// happened there.
    /// </summary>
    [Fact]
    public void A_refusal_of_a_remembered_place_names_the_creature_the_place_and_the_cause()
    {
        var details = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["placeX"] = 18,
            ["placeY"] = 7,
            ["sinceTick"] = 1703,
        };

        var panic = EventNarration.Sentence("refused_place_of_panic", details, JobKind.Cook, new GridPoint(15, 7));
        var wound = EventNarration.Sentence("refused_place_of_wound", details, JobKind.Haul, new GridPoint(15, 7));

        foreach (var sentence in new[] { panic, wound })
        {
            Assert.Contains("(18,7)", sentence, StringComparison.Ordinal);
            Assert.Contains("1703", sentence, StringComparison.Ordinal);
            Assert.Contains("(15,7)", sentence, StringComparison.Ordinal);
        }

        Assert.Contains("nerve broke", panic, StringComparison.Ordinal);
        Assert.Contains("put them down", wound, StringComparison.Ordinal);
        Assert.NotEqual(panic, wound);
    }

    /// <summary>
    /// The boundary, stated executably: the feed reads the reason code, it does
    /// not replace it. Deleting the code from the canonical state would break
    /// an invariant of ADR 0010 and this test, in that order.
    /// </summary>
    [Fact]
    public void The_canonical_state_still_carries_the_reason_code_the_feed_renders()
    {
        var state = PresentationFixtures.RunFixture("baseline", 1_400);
        var canonical = System.Text.Encoding.UTF8.GetString(PrototypeCanonical.Serialize(state));

        Assert.All(state.Creatures, creature =>
            Assert.False(string.IsNullOrWhiteSpace(creature.LastDecision.ReasonCode)));
        Assert.Contains("\"reasonCode\":", canonical, StringComparison.Ordinal);
        foreach (var code in state.Events.Select(@event => @event.ReasonCode).Distinct())
        {
            Assert.Contains($"\"{code}\"", canonical, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What a party actually reads like, printed rather than asserted. It is the
    /// artefact a person looks at to answer criterion 1 of Issue #117 — can the
    /// player name the creature, what happened to it and how that changed its
    /// next decision — without opening the inspector.
    /// </summary>
    [Fact]
    public void Report_the_story_of_one_party()
    {
        var state = PresentationFixtures.RunFixture("baseline", PrototypeTuning.SessionTicks);
        var interesting = state.Events
            .Where(@event => @event.ReasonCode is
                "combat_fled_morale" or "combat_downed" or "combat_returned" or
                "refused_place_of_panic" or "refused_place_of_wound")
            .Select(@event => $"t{@event.LastTick} · {EventNarration.Describe(state, @event)}");
        output.WriteLine(string.Join("\n", interesting));
    }
}
