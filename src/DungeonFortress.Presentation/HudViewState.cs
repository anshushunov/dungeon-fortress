using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// Everything the HUD text is a function of, and nothing else. It is a value, so
/// a test states a frame instead of driving an engine towards one.
///
/// The split this type draws is the point of the assembly: to the left of it live
/// canonical simulation state and the handful of adapter-owned facts the player
/// can see (which fixture, whether time is running, what is selected); to the
/// right lives only string building. Nothing here is a <c>Node</c>, a
/// <c>Viewport</c> or a <c>Label</c>, so nothing here needs Godot to be checked.
/// </summary>
/// <param name="Snapshot">The read-only projection of <c>PrototypeWorld</c>.</param>
/// <param name="Fixture">Which shipped command log the session was started from.</param>
/// <param name="Checksum">The canonical checksum of the current world.</param>
/// <param name="Paused">Whether the adapter is currently stepping time.</param>
/// <param name="Speed">The time multiplier shown while running.</param>
/// <param name="SelectedCreatureId">The creature the inspector is pointed at.</param>
/// <param name="SelectedCell">The map cell the inspector is pointed at.</param>
/// <param name="ControlFeedback">The last thing a brush or a command said.</param>
/// <param name="PlayerCommands">Indirect commands issued during this session.</param>
/// <param name="DiagnosticCount">How many runtime diagnostics have been recorded.</param>
public sealed record HudViewState(
    PrototypeSnapshot Snapshot,
    string Fixture,
    string Checksum,
    bool Paused,
    double Speed,
    int? SelectedCreatureId,
    GridPoint? SelectedCell,
    string ControlFeedback,
    IReadOnlyList<PrototypeCommand> PlayerCommands,
    int DiagnosticCount);

/// <summary>
/// The four HUD panels as text. One value carries them together so a caller
/// cannot build three of them and forget the fourth.
/// </summary>
public sealed record HudPanels(
    string Summary,
    string Inspector,
    string Feedback,
    string Roster);
