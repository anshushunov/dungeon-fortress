namespace DungeonFortress.Presentation;

/// <summary>
/// Which brush the player is holding. It used to be a private enum inside the
/// Godot adapter, which made every statement about a brush — its label, its
/// tooltip, what a stroke over a tile does — unreachable from a unit test.
///
/// The member names are load-bearing: <c>ui.editMode</c> is the enum name, and
/// <c>tests/golden/ui/*.json</c> records it. Renaming a member is a golden-state
/// change, not a refactor.
/// </summary>
public enum BrushMode
{
    Inspect,
    Paint,
    Erase,
    Dig,
    CancelDig,
    Build,
    CancelBuild,
}
