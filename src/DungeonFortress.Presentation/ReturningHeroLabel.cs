using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// The caption over a raider the domain has already met (Issue #358, slice 5 of
/// the pitch's order of proof).
///
/// <para><b>Only the returning one is captioned, and that is a decision rather
/// than a shortcut.</b> Every raider carries a name in the canonical snapshot —
/// he has to, or the one who comes back could not be the same person — but the
/// panel of a single creature has already been reported unreadable by the owner
/// (<see href="https://github.com/anshushunov/dungeon-fortress/issues/355">Issue
/// #355</see>), and twenty captions a party would make it worse in exactly the
/// place the complaint was about. The player is told about the raiders the
/// caption is a claim about: these are the ones you let go.</para>
///
/// <para>The text lives here and not in the adapter for the reason the whole of
/// this assembly exists: <c>Main.cs</c> is not built by the "Pure .NET" CI job,
/// so a sentence written there is a sentence nothing can check
/// (<see href="../../docs/decisions/0011-presentation-layer-without-engine.md">
/// ADR 0011</see>). It is a separate file from <c>HudText</c> and
/// <c>InspectorText</c> on purpose: this is a mark of the world layer, and those
/// two are panels.</para>
/// </summary>
public static class ReturningHeroLabel
{
    /// <summary>
    /// Whether this raider gets a caption at all: he is on the map, he is not a
    /// body on the floor, and he has been here before.
    ///
    /// <para>A raider that has left through the gate is deliberately excluded:
    /// <see cref="RaiderMode.Escaped"/> means he is off the map, and a caption
    /// standing on the gate tile after he is gone would be the map answering a
    /// question nobody asked. A downed one is excluded for the opposite reason —
    /// the story of the return is over and the body already carries the mark that
    /// says so.</para>
    /// </summary>
    public static bool IsCaptioned(PrototypeRaiderSnapshot raider)
    {
        ArgumentNullException.ThrowIfNull(raider);
        return raider.ReturnedFromWave is not null && raider.Mode == RaiderMode.Raiding;
    }

    /// <summary>
    /// The name line: what he is called, and nothing else. It is a line of its own
    /// because it is the half the player is meant to recognise at a glance, and
    /// the half below it is the half he reads when he has stopped to look.
    /// </summary>
    public static string Name(PrototypeRaiderSnapshot raider)
    {
        ArgumentNullException.ThrowIfNull(raider);
        return raider.Name;
    }

    /// <summary>
    /// The line about the last encounter: which wave he walked out of, what the
    /// domain left on him and where — and <c>null</c> when there was no encounter
    /// to name.
    ///
    /// <para><b>A raider nobody reached gets no second line</b>, and that is the
    /// answer to the clutter this mark could otherwise be. Six survivors walk back
    /// into wave 4 of the shipped journal at once, and six two-line captions over
    /// six goblins standing in one corridor is the defect Issue #355 is about,
    /// arriving through a different door. The second line exists only where there
    /// is a past encounter to put in it — which in the simulation is exactly where
    /// there is a scar, because scar and remembered place are one decision.</para>
    ///
    /// <para>All three facts are published state rather than an interpretation of
    /// it: the wave is <c>returnedFromWave</c>, the scar is <c>scar</c>, the place
    /// is <c>rememberedPlace</c>.</para>
    /// </summary>
    public static string? Story(PrototypeRaiderSnapshot raider)
    {
        ArgumentNullException.ThrowIfNull(raider);
        return raider is { ReturnedFromWave: { } wave, RememberedPlace: { } place }
            ? $"волна {wave} · {ScarOf(raider.Scar)} ({place.Place.X},{place.Place.Y})"
            : null;
    }

    /// <summary>
    /// What the previous raid left on him, in the words the player is meant to
    /// take away. <see cref="InjuryKind.None"/> never reaches the caption — a
    /// raider without a scar has no remembered place either, and
    /// <see cref="Story"/> takes the other branch — but it is answered rather than
    /// thrown on, because a caption that throws is a frame that does not draw.
    /// </summary>
    public static string ScarOf(InjuryKind scar) => scar switch
    {
        InjuryKind.Heavy => "едва не добили",
        InjuryKind.Light => "достали",
        _ => "не тронули",
    };

    /// <summary>
    /// The lines of the caption, in order. Empty for a raider that is not
    /// captioned, so the adapter loops over the answer instead of asking twice.
    /// </summary>
    public static IReadOnlyList<string> Lines(PrototypeRaiderSnapshot raider) =>
        IsCaptioned(raider)
            ? Story(raider) is { } story ? [Name(raider), story] : [Name(raider)]
            : [];

    // -----------------------------------------------------------------------
    // Where the caption sits is no longer decided here (Issue #364).
    //
    // It used to be: Layout(state) walked the captioned raiders and gave each one
    // that would have collided the next band up — TopRefOf(slot), twenty
    // reference pixels per slot, with no ceiling. The band resolved the overlap
    // and lost the attachment: with four returning raiders in one corridor three
    // names stood in a column over nobody at all (evidence/364-before.png), and
    // the owner reported he could not tell whom he had met.
    //
    // Two further things were wrong with it and are worth recording, because both
    // are easy to reintroduce. It declared a collision from a 130-reference-pixel
    // centring box — six tiles wide — so raiders six tiles apart were separated
    // for an overlap that was never going to happen. And it could not see the
    // creature labels at all, which are the other half of the same frame.
    //
    // All of it now lives in WorldLabelLayout, which sees every label of the
    // frame at once. What stays here is what this file was always about: who is
    // captioned, and what the caption says.
    // -----------------------------------------------------------------------

    /// <summary>The size of the name line and of the story line under it.</summary>
    public const double NameTextRef = 8.0;

    /// <inheritdoc cref="NameTextRef"/>
    public const double StoryTextRef = 6.0;

    /// <summary>
    /// The rim drawn under the text. A caption without one is unreadable over a
    /// goblin — the same finding that put an outline under the damage numbers of
    /// Issue #210 — and it is a stroke rather than a plate, so nothing about this
    /// mark fills.
    /// </summary>
    public const double OutlineRef = 2.0;

    /// <summary>The colour of the name, and of the line under it.</summary>
    public const string NameColor = "#fca5a5";

    /// <inheritdoc cref="NameColor"/>
    public const string StoryColor = "#fecaca";

    /// <inheritdoc cref="NameColor"/>
    public const string OutlineColor = "#1c1917";
}
