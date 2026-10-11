using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;

namespace ScoreStudio.Core.Tests;

/// <summary>Drawing tuplets (docs/SPRINT4F_TUPLETS.md).</summary>
public sealed class TupletLayoutTests
{
    private const double Sp = 1.75;
    private const double NumberSize = 3.2;

    private static readonly char[] Noteheads = { Smufl.NoteheadDoubleWhole, Smufl.NoteheadWhole, Smufl.NoteheadHalf, Smufl.NoteheadBlack };

    private static bool IsTupletGlyph(char glyph) => glyph >= Smufl.Tuplet0 && glyph <= Smufl.TupletColon;

    /// <summary>Box of a tuplet number glyph, in mm.</summary>
    private static (double Left, double Right, double Top, double Bottom) Box(GlyphItem g) =>
        (g.X, g.X + Smufl.TupletGlyphWidth(g.Glyph) * g.Size / 4, g.Y - Smufl.TupletDigitHeight * g.Size / 4, g.Y);

    private static ScoreLayout Sample() =>
        ScoreLayoutEngine.Layout(MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", "tuplets.musicxml")).Project);

    [Fact]
    public void EveryTuplet_GetsItsNumber_OnTheStemSide_OutsideTheStaff_AndBracketsWhereItIsNotOneBeam()
    {
        var page = Sample().Pages.Single();
        var staffTop = page.Systems.Single().Staves.Single().Top;
        var numbers = page.Items.OfType<GlyphItem>().Where(g => IsTupletGlyph(g.Glyph)).OrderBy(g => g.X).ToList();
        var measures = page.Systems.Single().Measures;
        List<GlyphItem> In(int measure) => numbers.Where(g => g.X > measures[measure].Left && g.X < measures[measure].Right).ToList();
        bool Below(GlyphItem g) => g.Y > staffTop + 4 * Sp;

        // Measure 1: the stems-down beamed triplet below; the one with a rest above (its notes' stems go up); the
        // quarter-note triplet (stems down) below.
        Assert.Equal(new[] { true, false, true }, In(0).Select(Below));
        // Measure 2: placed below by the source; "3:2" as three glyphs; the nested pair above.
        Assert.Equal("3,3,:,2,3,3", string.Join(",", In(1).OrderBy(g => g.X).Select(g => g.Glyph == Smufl.TupletColon ? ":" : ((char)('0' + g.Glyph - Smufl.Tuplet0)).ToString())));
        Assert.Equal(new[] { true, true, true, true, false, false }, In(1).OrderBy(g => g.X).Select(Below));
        // Measure 3: high notes with stems down.
        Assert.True(Below(In(2).Single()));

        // Numbers sit outside the staff, half a space or more from its outer lines.
        Assert.All(numbers, g => Assert.True(Box(g).Bottom <= staffTop - 0.5 * Sp + 1e-9 || Box(g).Top >= staffTop + 4.5 * Sp - 1e-9, $"number at {g.Y:F2}"));

        // Brackets: the triplet with a rest, the quarter-note triplet and the outer nested one; two end hooks each.
        var hooks = page.Items.OfType<LineItem>().Where(l => l.X1 == l.X2 && Math.Abs(Math.Abs(l.Y2 - l.Y1) - 0.6 * Sp) < 1e-9
            && Math.Abs(l.Thickness - Smufl.TupletBracketThickness * Sp) < 1e-9).ToList();
        Assert.Equal(6, hooks.Count);

        // The nested triplet: the outer number is further from the notes than the inner one.
        var nested = In(1).OrderBy(g => g.X).Where(g => !Below(g)).ToList();
        Assert.Equal(2, nested.Count);
        Assert.True(nested.Min(g => g.Y) < nested.Max(g => g.Y) - Sp);
    }

    [Fact]
    public void NestedTripletNotes_AreWrittenAsTheValuesTheyStandFor()
    {
        // 2/9 of a quarter inside a triplet within a triplet is an eighth (2/9 × 3/2 × 3/2 = 1/2), not a 16th.
        var page = Sample().Pages.Single();
        var measure = page.Systems.Single().Measures[1];
        var beams = page.Items.OfType<BeamItem>().Where(b => b.X1 > measure.Left && b.X2 < measure.Right).ToList();
        Assert.Equal(3, beams.Count); // three beam groups of eighths: one beam each
    }

    /// <summary>
    /// In every score, each tuplet with a number draws it, and tuplet numbers never touch noteheads, rests, stems or
    /// beams.
    /// </summary>
    [Theory]
    [MemberData(nameof(NoteLayoutTests.AllScores), MemberType = typeof(NoteLayoutTests))]
    public void TupletNumbers_AreDrawn_AndTouchNoNotes_InEveryScore(string file)
    {
        var project = MusicXmlImporter.ImportFile(file).Project;
        var sp = project.Engraving?.StaffSpace ?? Sp;
        var layout = ScoreLayoutEngine.Layout(project);
        var expected = project.Parts.SelectMany(p => p.Measures).SelectMany(m => m.Tuplets)
            .Sum(t => t.ShowNumber switch { TupletNumber.None => 0, TupletNumber.Both => $"{t.Actual}:{t.Normal}".Length, _ => $"{t.Actual}".Length });
        var problems = new List<string>();
        var drawn = 0;
        foreach (var page in layout.Pages)
        {
            var numbers = page.Items.OfType<GlyphItem>().Where(g => IsTupletGlyph(g.Glyph)).Select(g => (Glyph: g, Box: Box(g))).ToList();
            drawn += numbers.Count;
            var others = new List<(string What, double Left, double Right, double Top, double Bottom)>();
            foreach (var g in page.Items.OfType<GlyphItem>())
            {
                if (Noteheads.Contains(g.Glyph))
                    others.Add(("notehead", g.X, g.X + 1.2 * sp, g.Y - 0.5 * sp, g.Y + 0.5 * sp));
                else if (g.Glyph >= Smufl.RestDoubleWhole && g.Glyph <= Smufl.RestDoubleWhole + 7)
                {
                    var type = (NoteType)(g.Glyph - Smufl.RestDoubleWhole);
                    var (low, high) = Smufl.RestBounds(type);
                    others.Add(("rest", g.X, g.X + Smufl.RestWidth(type) * sp, g.Y - high * sp, g.Y - low * sp));
                }
            }

            foreach (var l in page.Items.OfType<LineItem>().Where(l => l.X1 == l.X2 && Math.Abs(l.Thickness - Smufl.StemThickness * sp) < 1e-9))
                others.Add(("stem", l.X1 - l.Thickness / 2, l.X1 + l.Thickness / 2, Math.Min(l.Y1, l.Y2), Math.Max(l.Y1, l.Y2)));

            foreach (var (glyph, box) in numbers)
            {
                bool Overlaps(double left, double right, double top, double bottom) =>
                    Math.Min(box.Right, right) - Math.Max(box.Left, left) > 0.05 * sp && Math.Min(box.Bottom, bottom) - Math.Max(box.Top, top) > 0.05 * sp;
                foreach (var o in others.Where(o => Overlaps(o.Left, o.Right, o.Top, o.Bottom)))
                    problems.Add($"p{page.Number} number at ({glyph.X:F1}, {glyph.Y:F1}) on a {o.What} at ({o.Left:F1}, {o.Top:F1})");

                foreach (var beam in page.Items.OfType<BeamItem>().Where(b => b.X1 < box.Right && b.X2 > box.Left))
                {
                    var (from, to) = (Math.Max(beam.X1, box.Left), Math.Min(beam.X2, box.Right));
                    double Y(double x) => beam.Y1 + (beam.Y2 - beam.Y1) * (x - beam.X1) / (beam.X2 - beam.X1);
                    var (top, bottom) = (Math.Min(Y(from), Y(to)), Math.Max(Y(from), Y(to)) + beam.Thickness);
                    if (Overlaps(from, to, top, bottom))
                        problems.Add($"p{page.Number} number at ({glyph.X:F1}, {glyph.Y:F1}) on a beam");
                }
            }
        }

        Assert.True(drawn == expected, $"{Path.GetFileName(file)}: {drawn} tuplet number glyphs drawn, {expected} expected");
        Assert.True(problems.Count == 0, $"{Path.GetFileName(file)}: {problems.Count} collisions: {string.Join("; ", problems.Take(10))}");
    }
}
