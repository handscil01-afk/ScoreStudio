using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;

namespace ScoreStudio.Core.Tests;

public sealed class NoteLayoutTests
{
    private const double Sp = 1.75;

    private static readonly char[] Noteheads = { Smufl.NoteheadDoubleWhole, Smufl.NoteheadWhole, Smufl.NoteheadHalf, Smufl.NoteheadBlack };

    private static ScoreEvent Note(string spelling, int midi, int position, int duration = 1, int denominator = 1) => new()
    {
        Kind = "Note", NotationSpelling = spelling, MidiPitch = midi,
        PositionNumerator = position, PositionDenominator = denominator, DurationNumerator = duration, DurationDenominator = denominator,
    };

    private static ScoreProject OnePart(int keyFifths, params ScoreEvent[][] measures) => new()
    {
        Title = "",
        Parts =
        {
            new ScorePart
            {
                Name = "",
                Measures = measures.Select((events, i) => new ScoreMeasure { Number = i + 1, KeyFifths = keyFifths, Events = events.ToList() }).ToList(),
            },
        },
    };

    private static List<GlyphItem> Glyphs(ScoreLayout layout, params char[] glyphs) =>
        layout.Pages.SelectMany(p => p.Items).OfType<GlyphItem>().Where(g => glyphs.Contains(g.Glyph)).OrderBy(g => g.X).ToList();

    [Fact]
    public void Accidentals_FollowTheKeyAndLastUntilTheBarline()
    {
        // F major (one flat). Measure 1: B natural, B natural, Bb. Measure 2: B natural again.
        var project = OnePart(-1,
            new[] { Note("B4", 71, 0), Note("B4", 71, 1), Note("Bb4", 70, 2), Note("F4", 65, 3) },
            new[] { Note("B4", 71, 0, 4) });

        var layout = ScoreLayoutEngine.Layout(project);
        var naturals = Glyphs(layout, Smufl.AccidentalNatural);
        var flats = Glyphs(layout, Smufl.AccidentalFlat);
        var system = layout.Pages[0].Systems[0];

        Assert.Equal(2, naturals.Count);                                   // first B in each measure
        Assert.Single(flats, f => f.X > system.Measures[0].Left + 2 * Sp); // Bb after the naturals; the other flat is the key
        Assert.True(naturals[1].X > system.Measures[1].Left);
    }

    [Fact]
    public void TiedNote_DoesNotRepeatItsAccidentalAcrossTheBarline()
    {
        var tied = Note("F#4", 66, 3);
        tied.TieToNext = true;
        var project = OnePart(0,
            new[] { Note("C4", 60, 0, 3), tied },
            new[] { Note("F#4", 66, 0), Note("F#4", 66, 1, 3) });

        var sharps = Glyphs(ScoreLayoutEngine.Layout(project), Smufl.AccidentalSharp);

        // Measure 1's F# needs a sharp; the tied F# in measure 2 does not; the next F# in measure 2 does.
        Assert.Equal(2, sharps.Count);
    }

    [Fact]
    public void SimultaneousNotes_LineUpAcrossParts_AndLaterNotesStandFurtherRight()
    {
        var project = OnePart(0, new[] { Note("C5", 72, 0, 2), Note("D5", 74, 2), Note("E5", 76, 3) });
        project.Parts.Add(new ScorePart
        {
            Name = "",
            Staves = { },
            Measures = { new ScoreMeasure { Events = { Note("C4", 60, 0), Note("D4", 62, 1), Note("E4", 64, 2), Note("F4", 65, 3) } } },
        });

        var heads = Glyphs(ScoreLayoutEngine.Layout(project), Noteheads);
        var columns = heads.GroupBy(h => Math.Round(h.X, 6)).OrderBy(g => g.Key).ToList();

        Assert.Equal(7, heads.Count);
        Assert.Equal(new[] { 2, 1, 2, 2 }, columns.Select(c => c.Count())); // onsets 0, 1, 2, 3; part 1 skips beat 1
        var gaps = columns.Zip(columns.Skip(1), (a, b) => b.Key - a.Key).ToList();
        Assert.All(gaps, gap => Assert.True(gap > 2 * Sp));
    }

    [Fact]
    public void Stems_PointUpBelowTheMiddleLine_AndDownFromIt()
    {
        var project = OnePart(0, new[] { Note("E4", 64, 0), Note("B4", 71, 1), Note("C5", 72, 2), Note("G4", 67, 3) });
        var page = ScoreLayoutEngine.Layout(project).Pages[0];
        var heads = page.Items.OfType<GlyphItem>().Where(g => g.Glyph == Smufl.NoteheadBlack).OrderBy(g => g.X).ToList();
        var stems = page.Items.OfType<LineItem>().Where(l => l.X1 == l.X2 && Math.Abs(l.Thickness - Smufl.StemThickness * Sp) < 1e-9).OrderBy(l => l.X1).ToList();

        Assert.Equal(4, stems.Count);
        var up = heads.Zip(stems, (h, s) => s.X1 > h.X + Sp / 2 && Math.Min(s.Y1, s.Y2) < h.Y - 3 * Sp).ToList();
        Assert.Equal(new[] { true, false, false, true }, up); // E4 up, B4 (middle line) down, C5 down, G4 up
        // 3.5 spaces from the notehead centre; the line itself starts at the stem attachment point.
        Assert.All(stems, s => Assert.Equal((3.5 - Smufl.StemAttachY) * Sp, Math.Abs(s.Y2 - s.Y1), 6));
    }

    [Theory]
    [InlineData("E4", 64, 0)]
    [InlineData("C4", 60, 1)]
    [InlineData("A3", 57, 2)]
    [InlineData("A5", 81, 1)]
    [InlineData("C6", 84, 2)]
    public void LegerLines_AreAddedOutsideTheStaff(string spelling, int midi, int expected)
    {
        var page = ScoreLayoutEngine.Layout(OnePart(0, new[] { Note(spelling, midi, 0, 4) })).Pages[0];
        var legers = page.Items.OfType<LineItem>().Count(l => l.Y1 == l.Y2 && Math.Abs(l.Thickness - Smufl.LegerLineThickness * Sp) < 1e-9);
        Assert.Equal(expected, legers);
    }

    [Fact]
    public void WholeMeasureRest_IsCenteredInTheMeasure()
    {
        var rest = new ScoreEvent { Kind = "Rest", DurationNumerator = 3, DurationDenominator = 1 };
        var project = OnePart(0, new[] { rest });
        project.Parts[0].Measures[0].Numerator = 3;

        var page = ScoreLayoutEngine.Layout(project).Pages[0];
        var glyph = page.Items.OfType<GlyphItem>().Single(g => g.Glyph == Smufl.Rest(NoteType.Whole));
        var measure = page.Systems[0].Measures[0];
        var center = glyph.X + Smufl.RestWidth(NoteType.Whole) * Sp / 2;

        Assert.True(center > measure.Left + (measure.Right - measure.Left) * 0.4 && center < measure.Right);
        Assert.Equal(page.Systems[0].Staves[0].Top + Sp, glyph.Y, 9); // hangs from the fourth line
    }

    [Fact]
    public void ChordSeconds_PutOneHeadOnTheOtherSideOfTheStem()
    {
        var project = OnePart(0, new[] { Note("F4", 65, 0, 4), Note("G4", 67, 0, 4) });
        var heads = Glyphs(ScoreLayoutEngine.Layout(project), Smufl.NoteheadWhole);

        Assert.Equal(2, heads.Count);
        Assert.Equal(Smufl.NoteheadWidth(NoteType.Whole) * Sp, heads[1].X - heads[0].X, 9);
    }

    [Theory]
    [InlineData("piano-grand-staff.musicxml", 4)]       // D major on two staves
    [InlineData("simple-melody.musicxml", 1)]           // F major
    [InlineData("hymn-repeats-chords.musicxml", 1)]     // G major
    public void KeySignature_IsDrawnAfterTheClefOnEveryStaff(string file, int accidentals)
    {
        var project = MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", file)).Project;
        var page = ScoreLayoutEngine.Layout(project).Pages[0];
        var system = page.Systems[0];
        var headerEnd = system.Measures[0].Left;

        var keyGlyphs = page.Items.OfType<GlyphItem>().Count(g =>
            g.Glyph is Smufl.AccidentalSharp or Smufl.AccidentalFlat && g.X < headerEnd && g.X > system.Left + 3 * Sp);
        Assert.Equal(accidentals, keyGlyphs);
    }

    [Theory]
    [InlineData("simple-melody.musicxml")]
    [InlineData("piano-grand-staff.musicxml")]
    [InlineData("unsupported-features.musicxml")]
    [InlineData("hymn-repeats-chords.musicxml")]
    [InlineData("layout-features.musicxml")]
    public void EveryImportedNoteGetsAHead_AndEveryRestAGlyph(string file)
    {
        var project = MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", file)).Project;
        var items = ScoreLayoutEngine.Layout(project).Pages.SelectMany(p => p.Items).OfType<GlyphItem>().ToList();

        // Notes and rests on hidden staves are not drawn.
        var shown = project.Parts.SelectMany(p => p.Measures).SelectMany(m => m.Events.Where(e => !m.HiddenStaves.Contains(e.Staff))).ToList();
        Assert.Equal(shown.Count(e => e.Kind == "Note"), items.Count(g => Noteheads.Contains(g.Glyph)));
        Assert.Equal(shown.Count(e => e.Kind == "Rest"), items.Count(g => g.Glyph >= Smufl.RestDoubleWhole && g.Glyph <= Smufl.RestDoubleWhole + 7));
    }

    private static ScoreLayout LayoutFeatures() =>
        ScoreLayoutEngine.Layout(MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", "layout-features.musicxml")).Project);

    private static List<GlyphItem> GlyphsIn(PageLayout page, double left, double right, double top, double bottom, params char[] glyphs) =>
        page.Items.OfType<GlyphItem>()
            .Where(g => glyphs.Contains(g.Glyph) && g.X >= left && g.X < right && g.Y >= top && g.Y <= bottom)
            .OrderBy(g => g.X).ToList();

    [Fact]
    public void LayoutFeatures_FollowTheSourceLineAndPageBreaks_AndLeaveOutHiddenStaves()
    {
        var layout = LayoutFeatures();

        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(
            new[] { "1-2:P1", "3-4:P1,P2", "5-5:P1,P2" },
            layout.Pages.SelectMany(p => p.Systems).Select(s =>
                $"{s.Measures[0].MeasureIndex + 1}-{s.Measures[^1].MeasureIndex + 1}:{string.Join(",", s.Staves.Select(st => $"P{st.PartIndex + 1}"))}"));
        Assert.Equal(2, layout.Pages[0].Systems.Count);
        Assert.Equal(5, layout.Pages[1].Systems.Single().Measures[0].MeasureIndex + 1);
    }

    [Fact]
    public void LayoutFeatures_SystemsStartWithTheClefInForce_AndChangesAreDrawnSmallWhereTheyHappen()
    {
        var page = LayoutFeatures().Pages[0];
        var (first, second) = (page.Systems[0], page.Systems[1]);
        var clefs = new[] { Smufl.GClef, Smufl.FClef };
        var changeSize = 4 * Sp * Smufl.ClefChangeScale;

        Assert.Equal(ClefType.Treble, first.Staves[0].Clef);
        Assert.Equal(ClefType.Treble, second.Staves[0].Clef); // measure 3 changes back to treble right at the system start
        Assert.Equal(ClefType.Bass, second.Staves[1].Clef);

        // Measure 2: a small bass clef before its first note, inside the measure's space rather than in the header.
        var m2 = first.Measures[1];
        var m2Clef = Assert.Single(GlyphsIn(page, m2.Left - Sp, m2.Right, first.Top, first.Bottom, clefs), g => g.Size < 4 * Sp);
        Assert.Equal((Smufl.FClef, changeSize), (m2Clef.Glyph, m2Clef.Size));
        var m2Heads = GlyphsIn(page, m2.Left, m2.Right, first.Top - 10 * Sp, first.Bottom + 10 * Sp, Noteheads);
        Assert.True(m2Clef.X < m2Heads[0].X);

        // Measure 3 starts the second system with a full-size treble clef and no extra small clef.
        var staffTop = second.Staves[0].Top;
        var m3 = second.Measures[0];
        Assert.DoesNotContain(GlyphsIn(page, second.Left, m3.Right, staffTop - 2 * Sp, staffTop + 6 * Sp, clefs), g => g.Size < 4 * Sp);

        // Measure 4: the bass clef comes on beat 3, between the half notes.
        var m4 = second.Measures[1];
        var m4Clef = Assert.Single(GlyphsIn(page, m4.Left, m4.Right, staffTop - 2 * Sp, staffTop + 6 * Sp, clefs));
        Assert.Equal((Smufl.FClef, changeSize), (m4Clef.Glyph, m4Clef.Size));
        var m4Heads = GlyphsIn(page, m4.Left, m4.Right, staffTop - 10 * Sp, staffTop + 10 * Sp, Noteheads);
        Assert.Equal(2, m4Heads.Count);
        Assert.True(m4Heads[0].X < m4Clef.X && m4Clef.X < m4Heads[1].X);

        // After the change the C3 half note sits in the bass staff's second space from the bottom.
        Assert.Equal(staffTop + 2.5 * Sp, m4Heads[1].Y, 6);
    }

    [Fact]
    public void LayoutFeatures_OctaveLine_WritesNotesAnOctaveLower_AndDrawsTheLabelAboveTheStaff()
    {
        var page = LayoutFeatures().Pages[0];
        var system = page.Systems[1];
        var staffTop = system.Staves[0].Top;
        var m3 = system.Measures[0];

        var label = Assert.Single(page.Items.OfType<GlyphItem>(), g => g.Glyph == Smufl.OttavaAlta);
        Assert.True(label.Y < staffTop, "8va label above the staff");
        Assert.InRange(label.X, m3.Left, m3.Right);

        // Sounding C6 D6 E6 F6 are written as C5 D5 E5 F5 (third space up to top line); the whole-note A5 as A4.
        var heads = GlyphsIn(page, m3.Left, m3.Right, staffTop - 10 * Sp, staffTop + 10 * Sp, Noteheads);
        var quarters = heads.Where(h => h.Glyph == Smufl.NoteheadBlack).Select(h => Math.Round((h.Y - staffTop) / Sp, 3));
        Assert.Equal(new[] { 1.5, 1.0, 0.5, 0.0 }, quarters);
        Assert.Equal(2.5, Math.Round((heads.Single(h => h.Glyph == Smufl.NoteheadWhole).Y - staffTop) / Sp, 3));

        // The dashed line ends with a hook down to the staff, before measure 4.
        var lines = page.Items.OfType<LineItem>().Where(l => l.Y1 < staffTop && l.Y1 > staffTop - 6 * Sp && l.Y2 < staffTop && l.X1 > label.X).ToList();
        Assert.NotEmpty(lines);
        Assert.True(lines.Max(l => Math.Max(l.X1, l.X2)) <= system.Measures[1].Left + Sp);
        Assert.Contains(lines, l => Math.Abs(l.X1 - l.X2) < 1e-9 && l.Y2 > l.Y1);
    }

    [Fact]
    public void SampleProject_StaysInsideThePage_WithNotes()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml"), "*.musicxml"))
        {
            var layout = ScoreLayoutEngine.Layout(MusicXmlImporter.ImportFile(file).Project);
            foreach (var page in layout.Pages)
            {
                foreach (var item in page.Items)
                {
                    var (x, y) = item switch { LineItem l => (Math.Min(l.X1, l.X2), Math.Min(l.Y1, l.Y2)), GlyphItem g => (g.X, g.Y), TextItem t => (t.X, t.Y), _ => (0, 0) };
                    Assert.True(x >= 0 && y >= 0 && x <= page.Width && y <= page.Height, $"{Path.GetFileName(file)}: {item}");
                }
            }
        }
    }
}
