using DungeonFortress.Simulation;
using Xunit;

namespace DungeonFortress.Presentation.Tests;

public sealed class ZzProbe
{
    private static PrototypeSnapshot Scene(int target)
    {
        var log = PresentationFixtures.LogOf("baseline") with { Seed = 20260729UL };
        var world = new PrototypeWorld(log);
        while (!world.IsComplete && world.CurrentTick < target)
        {
            world.Step();
        }

        return world.GetSnapshot();
    }

    [Fact]
    public void Dump()
    {
        var lines = new List<string>();
        foreach (var target in new[] { 2025, 2380 })
        {
            var state = Scene(target);
            var focus = WorldLabelFocus.None;
            var requests = WorldLabels.Requests(state, focus, CameraView.DefaultTileSize);
            var placed = WorldLabels.Of(state, focus, CameraView.DefaultTileSize);
            lines.Add($"t{target}: asked {requests.Count} placed {placed.Count}");
            foreach (var r in requests)
            {
                var p = placed.FirstOrDefault(item => item.Request.Subject == r.Subject);
                lines.Add($"  {r.Subject.Kind}#{r.Subject.Id} «{r.Lines[0].Text}» w={WorldLabelLayout.WidthRef(r.Lines):F1} rank={r.Rank} -> {(p is null ? "DROPPED" : $"{p.Alignment} att={p.AttachmentRef:F1} lines={p.Lines.Count}")}");
            }
        }

        Assert.Fail(string.Join(" | ", lines));
    }
}
