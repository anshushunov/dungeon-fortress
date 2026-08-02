using DungeonFortress.Simulation;

namespace DungeonFortress.Presentation;

/// <summary>
/// The purpose glyph of a room, as strokes in a unit square.
///
/// It is data rather than drawing code for the reason the whole of this assembly
/// exists: <c>Main.cs</c> is not built by the "Pure .NET" CI job, so a glyph
/// invented there is a glyph nothing can check. Here a test can say every purpose
/// has one, no two purposes share one, and every stroke stays inside the box the
/// adapter scales.
///
/// Strokes only, and that is a rule and not a style: the icon shares a cell with
/// whatever body is standing on it, and <see cref="OverlayMark.RoomLabel"/> is
/// declared <see cref="OverlayMarkPolicy.StrokeOnly"/> for that reason. A filled
/// glyph would be the fourth mark in this repository to land opaque on the very
/// creature it explains.
///
/// Coordinates are (0,0) top-left to (1,1) bottom-right, the same orientation as
/// the screen, so the adapter multiplies and translates and does nothing else.
/// </summary>
public static class RoomIcons
{
    private static readonly Dictionary<ZoneKind, IReadOnlyList<IReadOnlyList<ViewPoint>>> Glyphs =
        new()
        {
            // Three sprouts out of a furrow.
            [ZoneKind.Farm] =
            [
                [new(0.1, 0.9), new(0.9, 0.9)],
                [new(0.5, 0.9), new(0.5, 0.2)],
                [new(0.5, 0.5), new(0.2, 0.3)],
                [new(0.5, 0.5), new(0.8, 0.3)],
            ],
            // A pot with a lid.
            [ZoneKind.Kitchen] =
            [
                [new(0.2, 0.4), new(0.2, 0.85), new(0.8, 0.85), new(0.8, 0.4)],
                [new(0.1, 0.4), new(0.9, 0.4)],
                [new(0.5, 0.4), new(0.5, 0.15)],
            ],
            // A crate, banded.
            [ZoneKind.Larder] =
            [
                [new(0.15, 0.2), new(0.85, 0.2), new(0.85, 0.85), new(0.15, 0.85), new(0.15, 0.2)],
                [new(0.15, 0.45), new(0.85, 0.45)],
                [new(0.5, 0.45), new(0.5, 0.85)],
            ],
            // A bed: headboard, frame, pillow.
            [ZoneKind.Quarters] =
            [
                [new(0.1, 0.25), new(0.1, 0.8)],
                [new(0.1, 0.55), new(0.9, 0.55), new(0.9, 0.8)],
                [new(0.2, 0.4), new(0.45, 0.4)],
            ],
            // A training post: a stake with a crossbar.
            [ZoneKind.TrainingGround] =
            [
                [new(0.5, 0.15), new(0.5, 0.9)],
                [new(0.15, 0.4), new(0.85, 0.4)],
                [new(0.3, 0.9), new(0.7, 0.9)],
            ],
            // An eye.
            [ZoneKind.Watch] =
            [
                [new(0.1, 0.5), new(0.35, 0.25), new(0.65, 0.25), new(0.9, 0.5)],
                [new(0.1, 0.5), new(0.35, 0.75), new(0.65, 0.75), new(0.9, 0.5)],
                [new(0.42, 0.4), new(0.58, 0.4), new(0.58, 0.6), new(0.42, 0.6), new(0.42, 0.4)],
            ],
            // A refusal.
            [ZoneKind.Forbidden] =
            [
                [new(0.2, 0.2), new(0.8, 0.8)],
                [new(0.8, 0.2), new(0.2, 0.8)],
                [new(0.05, 0.5), new(0.95, 0.5)],
            ],
            // Two stacked blocks.
            [ZoneKind.MaterialStockpile] =
            [
                [new(0.1, 0.55), new(0.55, 0.55), new(0.55, 0.9), new(0.1, 0.9), new(0.1, 0.55)],
                [new(0.45, 0.2), new(0.9, 0.2), new(0.9, 0.55), new(0.45, 0.55), new(0.45, 0.2)],
            ],
        };

    /// <summary>
    /// The glyph of one purpose. It throws rather than returning nothing, because
    /// a purpose with no icon would draw a caption with a blank in front of it and
    /// look like a rendering fault instead of a missing declaration.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ViewPoint>> Of(ZoneKind purpose) =>
        Glyphs.TryGetValue(purpose, out var glyph)
            ? glyph
            : throw new ArgumentOutOfRangeException(
                nameof(purpose),
                purpose,
                "This purpose has no icon. A room says what it is for with a glyph " +
                "and a word; declare the glyph before the purpose can be a room.");

    public static IReadOnlyList<ZoneKind> Declared => [.. Glyphs.Keys.Order()];
}

/// <summary>
/// What a room says about itself in words: its name, and — when it is not simply
/// working — what is wrong with it.
///
/// The caption is the second half of the answer to Issue #52. The first half is
/// that the room is visibly a room; this is the half that stops it from being a
/// room whose purpose the player has to remember. ADR 0013 asks for both in the
/// same line: «иконка назначения и подпись с состоянием».
/// </summary>
public static class RoomLabels
{
    /// <summary>
    /// The short name of a purpose, in the same voice the map captions have used
    /// since the graybox: upper case, no punctuation, short enough to sit on one
    /// tile without covering the next one.
    /// </summary>
    public static string Name(ZoneKind purpose) => purpose switch
    {
        ZoneKind.Farm => "FARM",
        ZoneKind.Kitchen => "KITCHEN",
        ZoneKind.Larder => "LARDER",
        ZoneKind.Quarters => "QUARTERS",
        ZoneKind.TrainingGround => "TRAIN",
        ZoneKind.Watch => "WATCH",
        ZoneKind.Forbidden => "FORBIDDEN",
        ZoneKind.MaterialStockpile => "STOCKPILE",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
    };

    /// <summary>
    /// The name of the object a purpose needs, as the player would say it. Read
    /// from <c>PrototypeRooms.RequiredFeature</c> so the caption cannot name a
    /// requirement the simulation does not have.
    /// </summary>
    public static string FeatureName(TileKind feature) => feature switch
    {
        TileKind.Bed => "bed",
        TileKind.Kitchen => "stove",
        TileKind.Larder => "larder tile",
        TileKind.Bunk => "bunk",
        TileKind.Post => "post",
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
    };

    /// <summary>
    /// The caption of one room. A working room is its name and nothing else — the
    /// player is not made to read a status line for the ordinary case. Anything
    /// else says what is wrong, in the fewest words that are still an instruction:
    /// "TRAIN · no post" is a thing to go and do.
    /// </summary>
    public static string Caption(PrototypeRoomSnapshot room)
    {
        ArgumentNullException.ThrowIfNull(room);
        var name = Name(room.Purpose);
        return room.StatusCode switch
        {
            "room_missing_feature" => PrototypeRooms.RequiredFeature(room.Purpose) is { } feature
                ? $"{name} · no {FeatureName(feature)}"
                : $"{name} · unfinished",
            "room_blocked_priority" => $"{name} · off",
            "room_forbidden" => $"{name} · forbidden",
            _ => name,
        };
    }
}

/// <summary>
/// An object standing outside every room that could use it: a training post with
/// no <see cref="ZoneKind.TrainingGround"/> painted over it, a mushroom bed
/// outside the farm, a stove outside the kitchen.
/// </summary>
/// <param name="Position">Where it stands.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Needs">The purpose that would put it to work.</param>
public sealed record UnroomedObject(GridPoint Position, TileKind Kind, ZoneKind Needs);

/// <summary>
/// The other half of the silence ADR 0013 names.
///
/// A zone with no object in it says so on its own caption, because it is a room
/// and a room has a state. An object with no zone over it is not part of any room
/// by construction, so nothing would say anything about it at all — and that is
/// the case the ADR quotes: «столб стоит, работы <c>Drill</c> не появляются, и
/// игра об этом молчит». It is the ordinary state of the shipped fixture, where
/// four authored posts stand in the north store and no gym is painted.
///
/// Membership is asked of <see cref="MapProjection.IsInZone"/> rather than of the
/// rooms' contents, and the two are the same answer: a furniture tile inside a
/// zone is inside exactly one patch of that zone, and therefore inside exactly one
/// room's contents. Asking the projection is what folds a paint the player has
/// just made in a paused moment, so the warning clears the instant the gym is
/// painted instead of when time next moves.
/// <c>RoomReadingTests.A_furniture_tile_is_in_a_zone_exactly_when_a_room_holds_it</c>
/// pins the equivalence against a real session rather than asserting it here.
///
/// <b>What it does not reach.</b> Only the objects the snapshot publishes as
/// objects: mushroom beds and the stations, which are the stoves and every
/// training post, authored or built. Bunks and larder tiles are map features the
/// snapshot never lists on their own, so a bunk outside the quarters is not
/// reported. That is a limit of what is published, not a decision that those cases
/// do not matter, and closing it means publishing them — a change to the snapshot
/// with its own reason, not a side effect of this one.
/// </summary>
public static class RoomObjects
{
    /// <summary>
    /// The purpose that needs this kind of object, inverted from
    /// <c>PrototypeRooms.RequiredFeature</c> rather than written out again: a
    /// second copy of contract table 12.3 on this side of the seam is a second
    /// table to keep in step.
    /// </summary>
    public static ZoneKind? PurposeFor(TileKind feature)
    {
        foreach (var purpose in Enum.GetValues<ZoneKind>())
        {
            if (PrototypeRooms.RequiredFeature(purpose) == feature)
            {
                return purpose;
            }
        }

        return null;
    }

    public static IReadOnlyList<UnroomedObject> Unroomed(MapProjection view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var state = view.State;
        var found = new List<UnroomedObject>();

        foreach (var bed in state.Beds)
        {
            Consider(view, found, bed.Position, TileKind.Bed);
        }

        foreach (var station in state.Stations)
        {
            Consider(view, found, station.Position, station.Kind);
        }

        return [.. found.OrderBy(item => item.Position)];
    }

    private static void Consider(
        MapProjection view,
        List<UnroomedObject> found,
        GridPoint position,
        TileKind kind)
    {
        if (PurposeFor(kind) is not { } purpose || view.IsInZone(purpose, position))
        {
            return;
        }

        found.Add(new UnroomedObject(position, kind, purpose));
    }
}

/// <summary>
/// An erase accepted on this tick and not applied yet, asked of the projection
/// the way <see cref="MapProjection.IsPendingZonePaint"/> asks about a paint
/// (Issue #130).
///
/// The mirror exists because the two halves of the panel and the two halves of
/// the map must read the same fold: <c>InspectorText.DescribeRooms</c> names the
/// player's intent while it waits, and <c>Main.DrawZoneOutlines</c> draws the
/// cell being removed. Both read the erase through this type rather than from
/// the canonical zone, because the canonical zone still holds the cell — the
/// room only loses it when the tick runs.
/// </summary>
public static class PendingZoneMarks
{
    /// <summary>
    /// Whether a zone erase accepted on this tick removes this cell before the
    /// tick runs. The canonical zone still contains the cell; the fold has
    /// already taken it out.
    /// </summary>
    public static bool IsErasing(MapProjection view, ZoneKind zone, GridPoint cell)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.State.Zones[zone].Contains(cell) && !view.IsInZone(zone, cell);
    }

    /// <summary>
    /// The cells of one zone that an erase accepted on this tick removes, in
    /// the stable order the map draws per-cell marks in.
    /// </summary>
    public static IReadOnlyList<GridPoint> Erasures(MapProjection view, ZoneKind zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        return [.. view.State.Zones[zone]
            .Where(cell => !view.IsInZone(zone, cell))
            .Order()];
    }
}
