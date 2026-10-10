using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Core.Tests;

public sealed class ScoreLayoutEngineTests
{
    private static ScoreProject Sample() =>
        ProjectFileService.Load(Path.Combine(AppContext.BaseDirectory, "samples", "sample-choir.scoreproj"));

    private static ScoreProject WithMeasures(int count, params ClefType[][] partStaves)
    {
        var project = new ScoreProject { Title = "테스트" };
        for (var p = 0; p < partStaves.Length; p++)
        {
            project.Parts.Add(new ScorePart
            {
                Name = $"파트 {p + 1}",
                Staves = partStaves[p].Select(c => new ScoreStaff { Clef = c }).ToList(),
                Measures = Enumerable.Range(1, count).Select(n => new ScoreMeasure { Number = n }).ToList(),
            });
        }

        return project;
    }

    private static IEnumerable<SystemLayout> AllSystems(ScoreLayout layout) => layout.Pages.SelectMany(p => p.Systems);

    [Fact]
    public void Sample_EverySystemHasAllStavesWithTheirClefs()
    {
        var layout = ScoreLayoutEngine.Layout(Sample());

        foreach (var page in layout.Pages)
        {
            foreach (var system in page.Systems)
            {
                Assert.Equal(new[] { ClefType.Treble, ClefType.Treble, ClefType.Bass }, system.Staves.Select(s => s.Clef));
                foreach (var staff in system.Staves)
                {
                    var clefY = staff.Top + Smufl.ClefLine(staff.Clef) * 1.75;
                    Assert.Contains(page.Items.OfType<GlyphItem>(), g =>
                        g.Glyph == Smufl.ClefGlyph(staff.Clef) && Math.Abs(g.Y - clefY) < 1e-9 && Math.Abs(g.X - (system.Left + 1.75)) < 1e-9);
                    Assert.Equal(5, page.Items.OfType<LineItem>().Count(l =>
                        l.Y1 == l.Y2 && l.Y1 >= staff.Top - 1e-9 && l.Y1 <= staff.Top + 4 * 1.75 + 1e-9 && l.X1 == system.Left));
                }
            }
        }
    }

    [Fact]
    public void Sample_PianoStavesHaveABrace()
    {
        var layout = ScoreLayoutEngine.Layout(Sample());

        foreach (var page in layout.Pages)
            Assert.Equal(page.Systems.Count, page.Items.OfType<GlyphItem>().Count(g => g.Glyph == Smufl.Brace));
    }

    [Fact]
    public void EveryMeasureIsPlacedOnceInOrder_AndSystemsAreJustified()
    {
        var layout = ScoreLayoutEngine.Layout(WithMeasures(50, new[] { ClefType.Treble }));
        var systems = AllSystems(layout).ToList();

        Assert.Equal(Enumerable.Range(0, 50), systems.SelectMany(s => s.Measures).Select(m => m.MeasureIndex));
        foreach (var system in systems.SkipLast(1))
            Assert.Equal(210 - 15, system.Measures[^1].Right, 6);
        foreach (var system in systems)
            Assert.All(system.Measures.Zip(system.Measures.Skip(1)), pair => Assert.Equal(pair.First.Right, pair.Second.Left, 9));
    }

    [Fact]
    public void ManyMeasures_FlowOntoSeveralPages_WithPageNumbersFromPageTwo()
    {
        var layout = ScoreLayoutEngine.Layout(Sample());

        Assert.True(layout.Pages.Count >= 2, $"pages: {layout.Pages.Count}");
        Assert.DoesNotContain(layout.Pages[0].Items.OfType<TextItem>(), t => t.Text == "1");
        foreach (var page in layout.Pages.Skip(1))
            Assert.Contains(page.Items.OfType<TextItem>(), t => t.Text == page.Number.ToString() && t.Anchor == TextAnchor.End);
        Assert.All(layout.Pages, page => Assert.NotEmpty(page.Systems));
    }

    [Fact]
    public void NothingIsDrawnOutsideThePage_AndSystemsStayInsideTheMargins()
    {
        var layout = ScoreLayoutEngine.Layout(Sample());

        foreach (var page in layout.Pages)
        {
            foreach (var item in page.Items)
            {
                var (x1, y1, x2, y2) = item switch
                {
                    LineItem l => (Math.Min(l.X1, l.X2), Math.Min(l.Y1, l.Y2), Math.Max(l.X1, l.X2), Math.Max(l.Y1, l.Y2)),
                    // Bravura metadata: the brace sits on its baseline and is one em tall; the other glyphs
                    // reach at most 1.1 em above and 0.9 em below the baseline.
                    GlyphItem { Glyph: Smufl.Brace } b => (b.X, b.Y - b.Size, b.X + b.Size * Smufl.BraceAdvanceWidth / 4, b.Y),
                    GlyphItem g => (g.X, g.Y - 1.1 * g.Size, g.X + g.Size, g.Y + 0.9 * g.Size),
                    // Generous text width: every character one em wide.
                    TextItem t => t.Anchor switch
                    {
                        TextAnchor.Start => (t.X, t.Y - t.Size, t.X + t.Text.Length * t.Size, t.Y),
                        TextAnchor.End => (t.X - t.Text.Length * t.Size, t.Y - t.Size, t.X, t.Y),
                        _ => (t.X - t.Text.Length * t.Size / 2, t.Y - t.Size, t.X + t.Text.Length * t.Size / 2, t.Y),
                    },
                    _ => throw new InvalidOperationException(),
                };
                Assert.True(x1 >= 0 && y1 >= 0 && x2 <= page.Width && y2 <= page.Height, $"page {page.Number}: {item}");
            }

            foreach (var system in page.Systems)
            {
                Assert.True(system.Left >= 15 - 1e-9 && system.Right <= page.Width - 15 + 1e-9, $"system x {system.Left}..{system.Right}");
                Assert.True(system.Top >= 15 - 1e-9 && system.Bottom <= page.Height - 15 + 1e-9, $"system y {system.Top}..{system.Bottom}");
            }
        }
    }

    [Fact]
    public void TimeSignature_OnlyAtStartAndWhenItChanges()
    {
        var layout = ScoreLayoutEngine.Layout(Sample());
        var shown = AllSystems(layout).SelectMany(s => s.Measures).Where(m => m.ShowsTimeSignature).Select(m => m.MeasureIndex);

        Assert.Equal(new[] { 0, 16 }, shown);
        var digits = layout.Pages.SelectMany(p => p.Items).OfType<GlyphItem>().Count(g => g.Glyph >= Smufl.TimeSig0 && g.Glyph <= Smufl.TimeSig0 + 9);
        Assert.Equal(2 * 2 * 3, digits); // two time signatures × (numerator + denominator) × three staves
    }

    [Fact]
    public void LastMeasure_EndsWithFinalBarline()
    {
        var layout = ScoreLayoutEngine.Layout(WithMeasures(3, new[] { ClefType.Treble }));
        var page = layout.Pages.Single();
        var lastRight = page.Systems.Single().Measures[^1].Right;
        var thick = Smufl.ThickBarlineThickness * 1.75;

        Assert.Contains(page.Items.OfType<LineItem>(), l => l.X1 == l.X2 && Math.Abs(l.Thickness - thick) < 1e-9 && Math.Abs(l.X1 + thick / 2 - lastRight) < 1e-9);
        Assert.Single(page.Items.OfType<LineItem>(), l => Math.Abs(l.Thickness - thick) < 1e-9);
    }

    [Fact]
    public void EmptyScores_LayOutWithoutErrors()
    {
        var noParts = ScoreLayoutEngine.Layout(new ScoreProject { Title = "빈 악보" });
        Assert.Contains(noParts.Pages.Single().Items.OfType<TextItem>(), t => t.Text.Contains("파트가 없습니다"));

        var noMeasures = ScoreLayoutEngine.Layout(WithMeasures(0, new[] { ClefType.Bass }));
        var system = noMeasures.Pages.Single().Systems.Single();
        Assert.Empty(system.Measures);
        Assert.Equal(210 - 15, system.Right, 9);

        var noTitle = ScoreLayoutEngine.Layout(new ScoreProject { Title = "", Parts = { new ScorePart() } });
        Assert.Equal(15, noTitle.Pages.Single().Systems.Single().Top, 9);
    }

    [Fact]
    public void MultiLineComposer_GetsOneTextItemPerLine_AboveTheFirstSystem()
    {
        var project = WithMeasures(4, new[] { ClefType.Treble });
        project.Composer = "정윤용 작사 작곡\r\n김예림 조옮김\n\n편곡 홍길동";

        var page = ScoreLayoutEngine.Layout(project).Pages.Single();
        var lines = page.Items.OfType<TextItem>().Where(t => t.Anchor == TextAnchor.End && t.Size == 3.5).ToList();

        Assert.Equal(new[] { "정윤용 작사 작곡", "김예림 조옮김", "편곡 홍길동" }, lines.Select(t => t.Text));
        Assert.True(lines.Zip(lines.Skip(1)).All(pair => pair.Second.Y > pair.First.Y + 3.5), "lines must not overlap");
        Assert.True(page.Systems[0].Top - 2 * 1.75 > lines[^1].Y, "the last line must stay above the first system's measure numbers and clefs");
    }

    [Fact]
    public void NewProject_ShowsOneSystemWithFourMeasures()
    {
        var system = ScoreLayoutEngine.Layout(ScoreProject.CreateNew()).Pages.Single().Systems.Single();
        Assert.Equal(4, system.Measures.Count);
    }

    [Theory]
    [InlineData("A4", "Portrait", 210, 297)]
    [InlineData("A4", "Landscape", 297, 210)]
    [InlineData("Letter", "Portrait", 215.9, 279.4)]
    [InlineData("Letter", "Landscape", 279.4, 215.9)]
    [InlineData("B5", "Portrait", 210, 297)]
    public void PageSize_FollowsProjectSetting(string size, string orientation, double width, double height)
    {
        var project = WithMeasures(8, new[] { ClefType.Treble });
        project.PageSize = size;
        project.Orientation = orientation;

        var page = ScoreLayoutEngine.Layout(project).Pages[0];

        Assert.Equal((width, height), (page.Width, page.Height));
        Assert.True(page.Systems[0].Right <= width - 15 + 1e-9);
    }

    [Fact]
    public void Layout_DoesNotChangeTheProject()
    {
        var project = Sample();
        var before = System.Text.Json.JsonSerializer.Serialize(project);

        ScoreLayoutEngine.Layout(project);

        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(project));
    }
}
