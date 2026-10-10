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
    [InlineData("beams.musicxml")]
    [InlineData("part-groups.musicxml")]
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

    private static readonly char[] Flags = Enumerable.Range(0, 8).Select(i => (char)(Smufl.Flag8thUp + i)).ToArray();

    /// <summary>A measure's box on the page: its own width, and its system's band (halfway to the neighbouring systems).</summary>
    private sealed record MeasureBox(double Left, double Right, double Top, double Bottom)
    {
        public bool Contains(double x, double y) => x > Left && x < Right && y > Top && y < Bottom;
    }

    /// <summary>The beams sample fits on one page; its measures in order, whichever system they land in.</summary>
    private static (PageLayout Page, List<MeasureBox> Measures) BeamsSample()
    {
        var page = ScoreLayoutEngine.Layout(MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", "beams.musicxml")).Project).Pages.Single();
        var systems = page.Systems;
        return (page, systems.SelectMany((s, i) => s.Measures.Select(m => new MeasureBox(
            m.Left, m.Right,
            i == 0 ? 0 : (systems[i - 1].Bottom + s.Top) / 2,
            i == systems.Count - 1 ? page.Height : (s.Bottom + systems[i + 1].Top) / 2))).ToList());
    }

    /// <summary>Stems in a measure, left to right: vertical lines as thick as a stem.</summary>
    private static List<LineItem> Stems(PageLayout page, MeasureBox measure) =>
        page.Items.OfType<LineItem>()
            .Where(l => l.X1 == l.X2 && Math.Abs(l.Thickness - Smufl.StemThickness * Sp) < 1e-9 && measure.Contains(l.X1, (l.Y1 + l.Y2) / 2))
            .OrderBy(l => l.X1).ToList();

    private static List<BeamItem> Beams(PageLayout page, MeasureBox measure) =>
        page.Items.OfType<BeamItem>().Where(b => measure.Contains(b.X1, b.Y1) && measure.Contains(b.X2, b.Y2)).OrderBy(b => b.X1).ThenBy(b => b.Y1).ToList();

    /// <summary>The note end of a stem (where the heads are) and the free end (where a beam or flag goes).</summary>
    private static (double NoteEnd, double Tip, bool Up) Ends(LineItem stem) =>
        stem.Y2 < stem.Y1 ? (stem.Y1, stem.Y2, true) : (stem.Y1, stem.Y2, false);

    private static double TopEdgeAt(BeamItem beam, double x) => beam.Y1 + (beam.Y2 - beam.Y1) * (x - beam.X1) / (beam.X2 - beam.X1);

    [Fact]
    public void Beams_FollowTheSource_AndUnbeamedOrBrokenGroupsKeepTheirFlags()
    {
        var (page, measures) = BeamsSample();

        // 2 eighth groups, 2 dotted groups with a hook each, a 16th group (2 beams), 2 voices, and 16ths with a derived second beam.
        Assert.Equal(new[] { 2, 6, 0, 2, 2 }, measures.Select(m => Beams(page, m).Count));

        // Flags only on the two unbeamed eighths of measure 2 and the never-ended group of measure 3.
        var flags = page.Items.OfType<GlyphItem>().Where(g => Flags.Contains(g.Glyph)).ToList();
        Assert.Equal(new[] { 0, 2, 2, 0, 0 }, measures.Select(m => flags.Count(f => m.Contains(f.X, f.Y))));
    }

    [Fact]
    public void BeamedStems_ShareADirection_EndOnTheBeam_AndAreLongEnough()
    {
        var (page, measures) = BeamsSample();

        // Measure 1: C5–F5 sit high (stems down), G4–C5 sit low (stems up). Measure 4: voice 1 up, voice 2 down.
        Assert.Equal(new[] { false, false, false, false, true, true, true, true }, Stems(page, measures[0]).Select(s => Ends(s).Up));
        var m4 = Stems(page, measures[3]);
        Assert.Equal(5, m4.Count(s => Ends(s).Up));   // voice 1: four eighths and a half note
        Assert.Equal(5, m4.Count(s => !Ends(s).Up));  // voice 2 likewise

        foreach (var measure in measures)
        {
            var beams = Beams(page, measure);
            foreach (var stem in Stems(page, measure))
            {
                var spanning = beams.Where(b => b.X1 <= stem.X1 && b.X2 >= stem.X1).ToList();
                if (spanning.Count == 0) continue;

                // The stem ends on the outer edge of the outermost beam: the top edge for stems up, the bottom edge for stems down.
                var (noteEnd, tip, up) = Ends(stem);
                var outer = up ? spanning.Min(b => TopEdgeAt(b, stem.X1)) : spanning.Max(b => TopEdgeAt(b, stem.X1) + b.Thickness);
                Assert.Equal(outer, tip, 6);
                Assert.True(Math.Abs(tip - noteEnd) >= 3.5 * Sp - Smufl.StemAttachY * Sp - 1e-6, $"stem at {stem.X1:F1} is {Math.Abs(tip - noteEnd) / Sp:F2} spaces");
            }

            Assert.All(beams, b => Assert.True(Math.Abs(b.Y2 - b.Y1) <= 1.0 * Sp + 1e-9, "a beam rises at most one space"));
        }
    }

    [Fact]
    public void Hooks_PointTowardsTheirOwnBeat_AndAMissingSecondBeamIsDerivedFromTheNoteValues()
    {
        var (page, measures) = BeamsSample();
        var m2 = measures[1];
        var stems = Stems(page, m2);
        var hooks = Beams(page, m2).Where(b => b.X2 - b.X1 <= 1.2 * Sp).ToList();

        // A4. B4 (hook left from the 16th), C5 B4. (hook right from the 16th).
        Assert.Equal(2, hooks.Count);
        Assert.Equal(stems[1].X1 + stems[1].Thickness / 2, hooks[0].X2, 6);
        Assert.Equal(stems[2].X1 - stems[2].Thickness / 2, hooks[1].X1, 6);

        // Measure 5 stores only the first beam of its four 16ths; both beams still span the whole group.
        var m5 = measures[4];
        var m5Stems = Stems(page, m5).Take(4).ToList();
        Assert.All(Beams(page, m5), b =>
        {
            Assert.Equal(m5Stems[0].X1 - m5Stems[0].Thickness / 2, b.X1, 6);
            Assert.Equal(m5Stems[3].X1 + m5Stems[3].Thickness / 2, b.X2, 6);
        });
    }

    [Fact]
    public void PartGroups_DrawABracketAndJoinedBarlines_AndHiddenNamesStayHidden()
    {
        var project = MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", "part-groups.musicxml")).Project;
        var page = ScoreLayoutEngine.Layout(project).Pages.Single();
        var system = page.Systems[0];
        var (soprano, alto, pianoTop, pianoBottom) = (system.Staves[0].Top, system.Staves[1].Top, system.Staves[2].Top, system.Staves[3].Top);

        // The bracket ends sit on the top of the soprano staff and the bottom of the alto staff, left of the system.
        var top = Assert.Single(page.Items.OfType<GlyphItem>(), g => g.Glyph == Smufl.BracketTop);
        var bottom = Assert.Single(page.Items.OfType<GlyphItem>(), g => g.Glyph == Smufl.BracketBottom);
        Assert.Equal((soprano, alto + 4 * Sp), (top.Y, bottom.Y));
        Assert.True(top.X < system.Left && top.X == bottom.X);

        // Names: the choir parts are printed left of the bracket; the piano's name is hidden in the source.
        var names = page.Items.OfType<TextItem>().Where(t => t.Anchor == TextAnchor.End && t.X < system.Left).ToList();
        Assert.Equal(new[] { "소프라노", "알토" }, names.Select(t => t.Text));
        Assert.All(names, n => Assert.True(n.X < top.X));

        // Barlines after measure 1 run from the soprano through the alto, and separately through the piano.
        var barX = system.Measures[0].Right;
        var barlines = page.Items.OfType<LineItem>().Where(l => l.X1 == l.X2 && Math.Abs(l.X1 - barX) < Sp).Select(l => (Math.Min(l.Y1, l.Y2), Math.Max(l.Y1, l.Y2))).OrderBy(l => l.Item1).ToList();
        Assert.Equal(new[] { (soprano, alto + 4 * Sp), (pianoTop, pianoBottom + 4 * Sp) }, barlines);
    }

    /// <summary>
    /// Every MusicXML sample, plus the scores in the folder named by SCORESTUDIO_EXTRA_SCORES when it is set. That folder
    /// is for real scores that must not go into the repository (copyright): the same rules then run on them locally.
    /// </summary>
    public static TheoryData<string> AllScores()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml"), "*.musicxml").Order())
            data.Add(file);

        var extra = Environment.GetEnvironmentVariable("SCORESTUDIO_EXTRA_SCORES");
        if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra))
        {
            foreach (var file in Directory.GetFiles(extra).Where(f => f.EndsWith(".musicxml", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mxl", StringComparison.OrdinalIgnoreCase)).Order())
                data.Add(file);
        }

        return data;
    }

    /// <summary>Rules that hold for any score: beamed stems end exactly on their beam and carry no flag.</summary>
    [Theory]
    [MemberData(nameof(AllScores))]
    public void BeamedStemsEndOnTheirBeam_AndHaveNoFlag_InEveryScore(string file)
    {
        var layout = ScoreLayoutEngine.Layout(MusicXmlImporter.ImportFile(file).Project);
        var name = Path.GetFileName(file);
        foreach (var page in layout.Pages)
        {
            var beams = page.Items.OfType<BeamItem>().ToList();
            var flags = page.Items.OfType<GlyphItem>().Where(g => Flags.Contains(g.Glyph)).ToList();

            // Stems are vertical lines as thick as a stem and at least two spaces long (octave-line hooks are one space).
            var stems = page.Items.OfType<LineItem>().Where(l => l.X1 == l.X2
                && Math.Abs(l.Thickness - Smufl.StemThickness * Sp) < 1e-9 && Math.Abs(l.Y2 - l.Y1) > 2 * Sp);
            foreach (var stem in stems)
            {
                // A stem's beams are the ones it runs into (beams of other staves nearby do not count).
                var (noteEnd, tip, up) = Ends(stem);
                var (low, high) = (Math.Min(noteEnd, tip), Math.Max(noteEnd, tip));
                var onStem = beams.Where(b => b.X1 <= stem.X1 && b.X2 >= stem.X1)
                    .Where(b => TopEdgeAt(b, stem.X1) <= high + 1e-6 && TopEdgeAt(b, stem.X1) + b.Thickness >= low - 1e-6)
                    .ToList();
                if (onStem.Count == 0) continue;

                var outer = up ? onStem.Min(b => TopEdgeAt(b, stem.X1)) : onStem.Max(b => TopEdgeAt(b, stem.X1) + b.Thickness);
                Assert.True(Math.Abs(outer - tip) < 1e-6, $"{name} p{page.Number}: stem at ({stem.X1:F1}, {tip:F1}) mm misses its beam at {outer:F1}");
                Assert.DoesNotContain(flags, f => Math.Abs(f.X - (stem.X1 - stem.Thickness / 2)) < 1e-6 && Math.Abs(f.Y - tip) < 2 * Sp);
            }
        }
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
