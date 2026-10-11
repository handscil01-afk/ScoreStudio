using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ScoreStudio.Import;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Core.Tests;

public sealed class MusicXmlImporterTests : IDisposable
{
    private static readonly string SampleDirectory = Path.Combine(AppContext.BaseDirectory, "samples", "musicxml");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScoreStudioImportTests", Guid.NewGuid().ToString("N"));

    public MusicXmlImporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Sample(string name) => Path.Combine(SampleDirectory, name);

    private static string Fraction(int numerator, int denominator) => $"{numerator}/{denominator}";

    private static string Describe(ScoreEvent e) =>
        $"{e.Kind} {(e.Kind == "Note" ? e.NotationSpelling + "(" + e.MidiPitch + ")" : "")} @{Fraction(e.PositionNumerator, e.PositionDenominator)} " +
        $"len {Fraction(e.DurationNumerator, e.DurationDenominator)} staff {e.Staff} voice {e.Voice}{(Verse1(e) is { } lyric ? " '" + lyric + "'" : "")}";

    private static string? Verse1(ScoreEvent e) => e.Lyrics.FirstOrDefault(l => l.Verse == 1)?.Text;

    [Fact]
    public void SimpleMelody_TitlesKeyTimePitchesRhythmAndLyricsMatchTheSource()
    {
        var result = MusicXmlImporter.ImportFile(Sample("simple-melody.musicxml"));
        var project = result.Project;
        var part = Assert.Single(project.Parts);

        Assert.Equal(("도레미 노래", "ScoreStudio 샘플"), (project.Title, project.Composer));
        Assert.Equal(("노래", "Voice"), (part.Name, part.Instrument));
        Assert.Equal(ClefType.Treble, Assert.Single(part.Staves).Clef);
        Assert.Equal(new[] { 1, 2, 3, 4 }, part.Measures.Select(m => m.Number));
        Assert.All(part.Measures, m => Assert.Equal((3, 4, -1), (m.Numerator, m.Denominator, m.KeyFifths)));

        Assert.Equal(
            new[]
            {
                "Note F4(65) @0/1 len 1/1 staff 0 voice 1 '도'",
                "Note G4(67) @1/1 len 1/1 staff 0 voice 1 '레'",
                "Note A4(69) @2/1 len 1/1 staff 0 voice 1 '미'",
                "Note Bb4(70) @0/1 len 3/2 staff 0 voice 1 '파'",
                "Note A4(69) @3/2 len 1/2 staff 0 voice 1 '솔'",
                "Note G4(67) @2/1 len 1/1 staff 0 voice 1 '라'",
                "Rest  @0/1 len 1/1 staff 0 voice 1",
                "Note F4(65) @1/1 len 2/1 staff 0 voice 1 '시'",
                "Rest  @0/1 len 3/1 staff 0 voice 1",
            },
            part.Measures.SelectMany(m => m.Events).Select(Describe));

        Assert.Empty(result.Warnings);
        Assert.Equal((1, 4, 7, 2, 7), (result.Summary.Parts, result.Summary.Measures, result.Summary.Notes, result.Summary.Rests, result.Summary.Lyrics));
    }

    [Fact]
    public void PianoGrandStaff_StavesChordsBackupTripletsAndVoicesMatchTheSource()
    {
        var result = MusicXmlImporter.ImportFile(Sample("piano-grand-staff.musicxml"));
        var part = Assert.Single(result.Project.Parts);

        Assert.Equal("피아노 연습곡", result.Project.Title);
        Assert.Equal(new[] { ClefType.Treble, ClefType.Bass }, part.Staves.Select(s => s.Clef));
        Assert.All(part.Measures, m => Assert.Equal(2, m.KeyFifths));
        Assert.Equal(
            new[]
            {
                "Note D5(74) @0/1 len 1/1 staff 0 voice 1",
                "Note F#5(78) @1/1 len 1/1 staff 0 voice 1",
                "Note A5(81) @2/1 len 2/1 staff 0 voice 1",
                "Note D3(50) @0/1 len 4/1 staff 1 voice 5",
                "Note A3(57) @0/1 len 4/1 staff 1 voice 5",
            },
            part.Measures[0].Events.Select(Describe));
        Assert.Equal(
            new[]
            {
                "Note E5(76) @0/1 len 1/3 staff 0 voice 1",
                "Note F#5(78) @1/3 len 1/3 staff 0 voice 1",
                "Note G5(79) @2/3 len 1/3 staff 0 voice 1",
                "Note A5(81) @1/1 len 3/1 staff 0 voice 1",
                "Rest  @0/1 len 4/1 staff 1 voice 5",
            },
            part.Measures[1].Events.Select(Describe));

        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("잇단음표", warning.Message);
        Assert.Equal((1, "피아노 2마디"), (warning.Count, warning.FirstLocation));
    }

    [Fact]
    public void UnsupportedFeatures_AreReportedByKindWithCounts_AndGraceNotesAreSkipped()
    {
        var result = MusicXmlImporter.ImportFile(Sample("unsupported-features.musicxml"));
        var warnings = result.Warnings.ToDictionary(w => w.Message, w => w.Count);

        Assert.Equal(
            new Dictionary<string, int>
            {
                ["셈여림"] = 1,
                ["꾸밈음"] = 1,
                ["이음줄"] = 1,
                ["아티큘레이션(스타카토, 악센트 등)"] = 1,
                ["페르마타"] = 1,
            },
            warnings);
        Assert.Equal("플루트 1마디", result.Warnings.First(w => w.Message == "꾸밈음").FirstLocation);

        var part = result.Project.Parts.Single();
        Assert.Equal(ClefType.Treble, part.Staves.Single().Clef);
        Assert.DoesNotContain(part.Measures.SelectMany(m => m.Events), e => e.NotationSpelling == "D5");
        Assert.Equal(new[] { "노", null, "래" }, part.Measures[0].Events.Select(Verse1));

        // Imported since schema version 4: ties, later verses, hyphens, chord symbols, repeats.
        var first = part.Measures[0].Events[0];
        Assert.True(first.TieToNext);
        Assert.False(part.Measures[0].Events[1].TieToNext);
        Assert.Equal(new[] { "1:노:Begin", "2:둘:Single" }, first.Lyrics.Select(l => $"{l.Verse}:{l.Text}:{l.Syllabic}"));
        Assert.Equal("C", Assert.Single(part.Measures[0].ChordSymbols).Text);
        Assert.Equal((true, false), (part.Measures[0].RepeatStart, part.Measures[0].RepeatEnd));
        Assert.Equal((false, true), (part.Measures[1].RepeatStart, part.Measures[1].RepeatEnd));

        // Imported since schema version 5: the bass clef at the start of measure 2.
        var clefChange = Assert.Single(part.Measures[1].ClefChanges);
        Assert.Equal((0, 0, ClefType.Bass), (clefChange.Staff, clefChange.PositionNumerator, clefChange.Clef));
        Assert.Equal(new[] { "0/1", "2/1", "3/1" }, part.Measures[0].Events.Select(e => Fraction(e.PositionNumerator, e.PositionDenominator)));
    }

    /// <summary>
    /// Counts straight from the XML, independently of the importer, and compares with what was imported.
    /// </summary>
    [Theory]
    [InlineData("simple-melody.musicxml")]
    [InlineData("piano-grand-staff.musicxml")]
    [InlineData("unsupported-features.musicxml")]
    [InlineData("hymn-repeats-chords.musicxml")]
    [InlineData("layout-features.musicxml")]
    [InlineData("beams.musicxml")]
    [InlineData("part-groups.musicxml")]
    [InlineData("page-layout.musicxml")]
    [InlineData("lyrics-ties.musicxml")]
    public void ImportedCounts_MatchCountsTakenDirectlyFromTheSource(string file)
    {
        var source = XDocument.Load(Sample(file));
        var notes = source.Descendants("note").Where(n => n.Element("grace") is null && n.Element("cue") is null).ToList();
        var expected = (
            Parts: source.Root!.Elements("part").Count(),
            Measures: source.Root.Elements("part").Max(p => p.Elements("measure").Count()),
            Notes: notes.Count(n => n.Element("pitch") is not null),
            Rests: notes.Count(n => n.Element("rest") is not null),
            Lyrics: notes.Count(n => n.Elements("lyric").Any(l => l.Elements("text").Any(t => t.Value.Trim().Length > 0))),
            LyricSyllables: notes.Sum(n => n.Elements("lyric").Count(l => l.Elements("text").Any(t => t.Value.Trim().Length > 0))),
            Ties: notes.Count(n => n.Elements("tie").Any(t => (string?)t.Attribute("type") == "start")),
            Chords: source.Descendants("harmony").Count(h => h.Element("root") is not null),
            RepeatStarts: source.Descendants("repeat").Count(r => (string?)r.Attribute("direction") == "forward"),
            RepeatEnds: source.Descendants("repeat").Count(r => (string?)r.Attribute("direction") == "backward"),
            Voltas: source.Descendants("ending").Count(e => (string?)e.Attribute("type") == "start"),
            BeamGroups: notes.Count(n => n.Elements("beam").Any(b => (string?)b.Attribute("number") is null or "1" && b.Value == "begin")));

        var result = MusicXmlImporter.ImportFile(Sample(file));
        var measures = result.Project.Parts.SelectMany(p => p.Measures).ToList();
        var events = measures.SelectMany(m => m.Events).ToList();

        Assert.Equal(
            (expected.Parts, expected.Measures, expected.Notes, expected.Rests, expected.Lyrics, expected.Ties, expected.Chords),
            (result.Summary.Parts, result.Summary.Measures, result.Summary.Notes, result.Summary.Rests, result.Summary.Lyrics, result.Summary.Ties, result.Summary.ChordSymbols));
        Assert.Equal(expected.Notes, events.Count(e => e.Kind == "Note"));
        Assert.Equal(expected.Rests, events.Count(e => e.Kind == "Rest"));
        Assert.Equal(expected.LyricSyllables, events.Sum(e => e.Lyrics.Count));
        Assert.Equal(expected.Ties, events.Count(e => e.TieToNext));
        Assert.Equal(expected.Chords, measures.Sum(m => m.ChordSymbols.Count));
        Assert.Equal(expected.BeamGroups, events.Count(e => e.Beams.FirstOrDefault(BeamType.End) == BeamType.Begin && e.Beams.Count > 0));
        Assert.Equal(
            (expected.RepeatStarts, expected.RepeatEnds, expected.Voltas),
            (measures.Count(m => m.RepeatStart), measures.Count(m => m.RepeatEnd), measures.Count(m => m.Volta is not null)));
    }

    [Fact]
    public void Mxl_WithContainerXml_IsImported()
    {
        var path = Path.Combine(_dir, "with-container.mxl");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Sample("simple-melody.musicxml"), "score/melody.musicxml");
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/container.xml").Open());
            writer.Write("""<?xml version="1.0"?><container><rootfiles><rootfile full-path="score/melody.musicxml"/></rootfiles></container>""");
        }

        var result = MusicXmlImporter.ImportFile(path);

        Assert.Equal("도레미 노래", result.Project.Title);
        Assert.Equal(7, result.Summary.Notes);
    }

    [Fact]
    public void Mxl_WithoutContainerXml_UsesTheFirstScoreFile()
    {
        var path = Path.Combine(_dir, "no-container.mxl");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(Sample("piano-grand-staff.musicxml"), "piano.xml");

        Assert.Equal("피아노 연습곡", MusicXmlImporter.ImportFile(path).Project.Title);
    }

    [Fact]
    public void Hymn_VersesTiesChordSymbolsRepeatsAndVoltasMatchTheSource()
    {
        var result = MusicXmlImporter.ImportFile(Sample("hymn-repeats-chords.musicxml"));
        var measures = result.Project.Parts.Single().Measures;

        Assert.Empty(result.Warnings);
        Assert.Equal((7, 2, 1, 8), (result.Summary.Lyrics, result.Summary.LyricVerses, result.Summary.Ties, result.Summary.ChordSymbols));

        // Lyrics: two verses, a hyphenated word across the tie, an extender in verse 2.
        Assert.Equal(
            new[] { "1:주:Single 2:나:Single", "1:님:Single 2:의:Single", "1:사:Begin 2:삶:Single~", "", "1:랑:End", "1:해:Single", "1:요:Single", "", "1:멘:Single" },
            measures.SelectMany(m => m.Events).Select(e =>
                string.Join(" ", e.Lyrics.Select(l => $"{l.Verse}:{l.Text}:{l.Syllabic}{(l.Extend ? "~" : "")}"))));

        // The tie starts on the B4 at the end of measure 1 and nowhere else.
        var tied = Assert.Single(measures.SelectMany(m => m.Events), e => e.TieToNext);
        Assert.Equal(("B4", 1), (tied.NotationSpelling, measures.FindIndex(m => m.Events.Contains(tied)) + 1));

        Assert.Equal(
            new[] { "1:G@0/1", "1:D/F#@2/1", "2:Em7@0/1", "2:CM7@2/1", "3:Am7b5@0/1", "3:Gsus4@2/1", "3:N.C.@3/1", "4:Gadd9@0/1" },
            measures.SelectMany(m => m.ChordSymbols.Select(c => $"{m.Number}:{c.Text}@{c.PositionNumerator}/{c.PositionDenominator}")));
        var slash = measures[0].ChordSymbols[1];
        Assert.Equal(("D", 0, "major", "F", 1), (slash.RootStep, slash.RootAlter, slash.Kind, slash.BassStep, slash.BassAlter));

        Assert.Equal(
            new[] { "start", "end×2", "", "" },
            measures.Select(m => string.Join(",", new[] { m.RepeatStart ? "start" : null, m.RepeatEnd ? $"end×{m.RepeatTimes}" : null }.Where(x => x is not null))));
        Assert.Null(measures[0].Volta);
        Assert.Equal(("1", "1.", 1, false), (measures[1].Volta!.Numbers, measures[1].Volta!.Text, measures[1].Volta!.MeasureCount, measures[1].Volta!.OpenEnd));
        Assert.Equal(("2", "2.", 1, true), (measures[2].Volta!.Numbers, measures[2].Volta!.Text, measures[2].Volta!.MeasureCount, measures[2].Volta!.OpenEnd));
        Assert.Null(measures[3].Volta);
    }

    [Fact]
    public void LayoutFeatures_ClefChangesHiddenStavesBreaksAndOctaveLinesMatchTheSource()
    {
        var result = MusicXmlImporter.ImportFile(Sample("layout-features.musicxml"));
        var (melody, lower) = (result.Project.Parts[0], result.Project.Parts[1]);

        Assert.Empty(result.Warnings);
        Assert.Equal(("멜로디", "파트 2"), (melody.Name, lower.Name));

        // The first clef is the staff's clef; later ones are changes at the time they appear in the measure.
        Assert.Equal(ClefType.Treble, melody.Staves[0].Clef);
        Assert.Equal(
            new[] { "2: 0/1 Bass", "3: 0/1 Treble", "4: 2/1 Bass" },
            melody.Measures.SelectMany(m => m.ClefChanges.Select(c =>
                $"{m.Number}: {Fraction(c.PositionNumerator, c.PositionDenominator)} {c.Clef}")));
        Assert.All(lower.Measures, m => Assert.Empty(m.ClefChanges));

        // The 8va line (octave-shift type "down") covers both voices of measure 3 and stops before measure 4.
        // Pitches stay as sounded; OctaveShift only says how they are written.
        var m3 = melody.Measures[2].Events;
        Assert.Equal(new[] { 84, 86, 88, 89, 81 }, m3.Select(e => e.MidiPitch));
        Assert.Equal(new[] { "C6", "D6", "E6", "F6", "A5" }, m3.Select(e => e.NotationSpelling));
        Assert.All(m3, e => Assert.Equal(1, e.OctaveShift));
        Assert.All(melody.Measures.Where(m => m.Number != 3).SelectMany(m => m.Events), e => Assert.Equal(0, e.OctaveShift));

        Assert.Equal(new[] { "1:0", "2:0", "3:", "4:", "5:" }, lower.Measures.Select(m => $"{m.Number}:{string.Join(",", m.HiddenStaves)}"));
        Assert.All(melody.Measures, m => Assert.Empty(m.HiddenStaves));

        Assert.Equal(new[] { 3, 5 }, melody.Measures.Where(m => m.SystemBreak).Select(m => m.Number));
        Assert.Equal(new[] { 5 }, melody.Measures.Where(m => m.PageBreak).Select(m => m.Number));
    }

    [Fact]
    public void ChordSymbols_KeepTheirHeight_AndHiddenDegreesAreNotPrinted()
    {
        // Finale writes "C2/E" as kind text "2" plus an added 2nd marked print-object="no".
        const string xml = """
            <score-partwise>
              <part-list><score-part id="P1"><part-name>A</part-name></score-part></part-list>
              <part id="P1"><measure number="1"><attributes><divisions>1</divisions></attributes>
                <harmony default-y="26"><root><root-step>C</root-step></root><kind text="2">major</kind>
                  <bass><bass-step>E</bass-step></bass>
                  <degree print-object="no"><degree-value>2</degree-value><degree-alter>0</degree-alter><degree-type>add</degree-type></degree></harmony>
                <note><pitch><step>C</step><octave>5</octave></pitch><duration>2</duration></note>
                <harmony><root><root-step>G</root-step></root><kind>major</kind>
                  <degree><degree-value>9</degree-value><degree-alter>0</degree-alter><degree-type>add</degree-type></degree></harmony>
                <note><pitch><step>D</step><octave>5</octave></pitch><duration>2</duration></note>
              </measure></part>
            </score-partwise>
            """;

        var symbols = MusicXmlImporter.ImportXml(xml, "chords").Project.Parts[0].Measures[0].ChordSymbols;

        Assert.Equal(new[] { ("C2/E", (double?)2.6), ("Gadd9", null) }, symbols.Select(c => (c.Text, c.Y)));
    }

    [Fact]
    public void LyricPositions_AreReadFromDefaultY_AndPlacementAbove()
    {
        const string xml = """
            <score-partwise>
              <defaults><scaling><millimeters>7</millimeters><tenths>40</tenths></scaling><lyric-font font-family="Finale Lyrics" font-size="11.3"/></defaults>
              <part-list><score-part id="P1"><part-name>A</part-name></score-part></part-list>
              <part id="P1"><measure number="1"><attributes><divisions>1</divisions></attributes>
                <note><pitch><step>C</step><octave>5</octave></pitch><duration>1</duration>
                  <lyric number="1" default-y="-92"><text>가</text></lyric>
                  <lyric number="2" placement="above"><text>나</text></lyric>
                  <lyric number="3"><text>다</text></lyric></note>
                <note><pitch><step>D</step><octave>5</octave></pitch><duration>1</duration>
                  <lyric number="2"><extend type="stop"/></lyric></note>
              </measure></part>
            </score-partwise>
            """;

        var project = MusicXmlImporter.ImportXml(xml, "lyrics").Project;

        var lyrics = project.Parts[0].Measures[0].Events[0].Lyrics;
        Assert.Equal(new double?[] { -9.2, 2.5, null }, lyrics.Select(l => l.Y));
        Assert.Equal(new[] { 2 }, project.Parts[0].Measures[0].Events[1].ExtendEnds);
        Assert.Equal(11.3 * 25.4 / 72, project.Engraving!.LyricFontSize!.Value, 9);
    }

    [Fact]
    public void PageLayout_StaffSizeMarginsAndDistancesMatchTheSource()
    {
        var result = MusicXmlImporter.ImportFile(Sample("page-layout.musicxml"));
        var project = result.Project;
        var engraving = project.Engraving!;
        const double mmPerTenth = 7.9551 / 40;

        Assert.Empty(result.Warnings);
        Assert.Equal(("A4", "Portrait"), (project.PageSize, project.Orientation));
        Assert.Equal(10 * mmPerTenth, engraving.StaffSpace, 9);
        Assert.All(new[] { engraving.MarginLeft, engraving.MarginRight, engraving.MarginTop, engraving.MarginBottom },
            m => Assert.Equal(101 * mmPerTenth, m, 9));
        Assert.Equal((7.2, 3.3, 8.0), (engraving.SystemDistance, engraving.TopSystemDistance, engraving.StaffDistance));

        // Per-system values, in staff spaces, stored on the measure that starts the system.
        var (upper, lower) = (project.Parts[0].Measures, project.Parts[1].Measures);
        Assert.Equal(new double?[] { 31.3, null, 10 }, upper.Select(m => m.TopSystemDistance));
        Assert.Equal(new double?[] { null, 17.4, null }, upper.Select(m => m.SystemDistance));
        Assert.Equal("0:14.5", string.Join(";", lower[1].StaffDistances.Select(d => $"{d.Staff}:{d.Distance}")));
        Assert.All(upper.Concat(lower).Where(m => m != lower[1]), m => Assert.Empty(m.StaffDistances));
    }

    [Theory]
    [InlineData(1100, 1405, "Letter", "Portrait", null)]
    [InlineData(1405, 1100, "Letter", "Landscape", null)]
    [InlineData(1056, 1494, "A4", "Portrait", null)]
    [InlineData(1290, 1820, "A4", "Portrait", "지원하지 않는 용지 크기(257×362mm)는 A4로 표시함")]
    public void PageSize_IsTakenFromTheSourcePage(int width, int height, string size, string orientation, string? warning)
    {
        var xml = $"""
            <score-partwise>
              <defaults><scaling><millimeters>7.9551</millimeters><tenths>40</tenths></scaling>
                <page-layout><page-height>{height}</page-height><page-width>{width}</page-width></page-layout></defaults>
              <part-list><score-part id="P1"><part-name>A</part-name></score-part></part-list>
              <part id="P1"><measure number="1"/></part>
            </score-partwise>
            """;

        var result = MusicXmlImporter.ImportXml(xml, "page");

        Assert.Equal((size, orientation), (result.Project.PageSize, result.Project.Orientation));
        Assert.Equal(warning, result.Warnings.SingleOrDefault()?.Message);
    }

    [Fact]
    public void WithoutScaling_TheProjectKeepsDefaultLayout()
    {
        var project = MusicXmlImporter.ImportFile(Sample("simple-melody.musicxml")).Project;

        Assert.Null(project.Engraving);
        Assert.Equal(("A4", "Portrait"), (project.PageSize, project.Orientation));
    }

    [Fact]
    public void PartGroupsAndHiddenNames_MatchTheSource()
    {
        var result = MusicXmlImporter.ImportFile(Sample("part-groups.musicxml"));

        Assert.Empty(result.Warnings);
        Assert.Equal(new[] { ("소프라노", true), ("알토", true), ("피아노", false) }, result.Project.Parts.Select(p => (p.Name, p.ShowName)));
        var group = Assert.Single(result.Project.PartGroups);
        Assert.Equal((0, 1, PartGroupSymbol.Bracket, true), (group.FirstPart, group.LastPart, group.Symbol, group.JoinBarlines));
    }

    [Fact]
    public void NestedAndUnclosedPartGroups_AreReadByNumber()
    {
        // Group 1 (brace, no barline) holds all four parts; group 2 (square, Mensurstrich) holds parts 2–3;
        // group 3 (no symbol) starts at part 4 and is never stopped.
        const string xml = """
            <score-partwise>
              <part-list>
                <part-group number="1" type="start"><group-symbol>brace</group-symbol><group-barline>no</group-barline></part-group>
                <score-part id="P1"><part-name>A</part-name></score-part>
                <part-group number="2" type="start"><group-symbol>square</group-symbol><group-barline>Mensurstrich</group-barline></part-group>
                <score-part id="P2"><part-name>B</part-name></score-part>
                <score-part id="P3"><part-name>C</part-name><part-name-display print-object="no"><display-text>C</display-text></part-name-display></score-part>
                <part-group number="2" type="stop"/>
                <part-group number="3" type="start"/>
                <score-part id="P4"><part-name>D</part-name></score-part>
                <part-group number="1" type="stop"/>
              </part-list>
              <part id="P1"><measure number="1"/></part>
              <part id="P2"><measure number="1"/></part>
              <part id="P3"><measure number="1"/></part>
              <part id="P4"><measure number="1"/></part>
            </score-partwise>
            """;

        var result = MusicXmlImporter.ImportXml(xml, "groups");

        Assert.Equal(
            new[] { (1, 2, PartGroupSymbol.Square, false), (0, 3, PartGroupSymbol.Brace, false), (3, 3, PartGroupSymbol.None, false) },
            result.Project.PartGroups.Select(g => (g.FirstPart, g.LastPart, g.Symbol, g.JoinBarlines)));
        Assert.Equal(new[] { true, true, false, true }, result.Project.Parts.Select(p => p.ShowName));
        Assert.Equal("끝나지 않은 파트 묶음은 마지막 파트까지로 맞춤", Assert.Single(result.Warnings).Message);
    }

    [Fact]
    public void Beams_AreStoredPerLevelAsInTheSource()
    {
        var result = MusicXmlImporter.ImportFile(Sample("beams.musicxml"));
        var measures = result.Project.Parts.Single().Measures;

        Assert.Empty(result.Warnings);
        string Beams(ScoreEvent e) => e.Beams.Count == 0 ? "-" : string.Join("+", e.Beams);
        Assert.Equal(
            new[] { "Begin", "End+BackwardHook", "Begin+ForwardHook", "End", "Begin+Begin", "Continue+Continue", "Continue+Continue", "End+End", "-", "-" },
            measures[1].Events.Select(Beams));
        Assert.Equal(new[] { "Begin", "Continue", "-", "-" }, measures[2].Events.Select(Beams)); // never ended: stored as written
        Assert.Equal(new[] { "Begin", "Continue", "Continue", "End" }, measures[4].Events.Take(4).Select(Beams));
    }

    [Theory]
    [InlineData("""<beam number="1">sideways</beam>""")]
    [InlineData("""<beam number="2">begin</beam>""")]
    [InlineData("""<beam number="1">begin</beam><beam number="1">end</beam>""")]
    public void UnreadableBeams_AreDroppedWithAWarning(string beams)
    {
        var xml = $"""
            <score-partwise>
              <part-list><score-part id="P1"><part-name>A</part-name></score-part></part-list>
              <part id="P1"><measure number="1"><attributes><divisions>2</divisions></attributes>
                <note><pitch><step>C</step><octave>5</octave></pitch><duration>1</duration>{beams}</note>
              </measure></part>
            </score-partwise>
            """;

        var result = MusicXmlImporter.ImportXml(xml, "beams");

        Assert.Empty(result.Project.Parts.Single().Measures.Single().Events.Single().Beams);
        Assert.Equal("읽을 수 없는 빔(음표 잇기) 정보는 무시함", Assert.Single(result.Warnings).Message);
    }

    [Fact]
    public void OctaveLineStop_IncludesTheNoteStartingAtTheStop_AsFinaleWritesIt()
    {
        // Measure 1: 8va from beat 2; the stop sits before the third quarter, which is still under the line.
        // Measure 2 (staff 2): an 8vb whose stop comes before the first chord of measure 3, as Finale writes a line
        // that ends on a downbeat.
        const string xml = """
            <score-partwise>
              <part-list><score-part id="P1"><part-name>A</part-name></score-part></part-list>
              <part id="P1">
                <measure number="1">
                  <attributes><divisions>1</divisions><staves>2</staves></attributes>
                  <note><pitch><step>C</step><octave>5</octave></pitch><duration>1</duration><staff>1</staff></note>
                  <direction><direction-type><octave-shift type="down" size="8"/></direction-type><staff>1</staff></direction>
                  <note><pitch><step>C</step><octave>6</octave></pitch><duration>1</duration><staff>1</staff></note>
                  <direction><direction-type><octave-shift type="stop" size="8"/></direction-type><staff>1</staff></direction>
                  <note><pitch><step>G</step><octave>6</octave></pitch><duration>1</duration><staff>1</staff></note>
                  <note><pitch><step>A</step><octave>5</octave></pitch><duration>1</duration><staff>1</staff></note>
                  <backup><duration>4</duration></backup>
                  <note><pitch><step>C</step><octave>3</octave></pitch><duration>4</duration><staff>2</staff></note>
                </measure>
                <measure number="2">
                  <note><rest/><duration>4</duration><staff>1</staff></note>
                  <backup><duration>4</duration></backup>
                  <direction><direction-type><octave-shift type="up" size="8"/></direction-type><staff>2</staff></direction>
                  <note><pitch><step>C</step><octave>1</octave></pitch><duration>4</duration><staff>2</staff></note>
                </measure>
                <measure number="3">
                  <note><rest/><duration>4</duration><staff>1</staff></note>
                  <backup><duration>4</duration></backup>
                  <direction><direction-type><octave-shift type="stop" size="8"/></direction-type><staff>2</staff></direction>
                  <note><pitch><step>C</step><octave>1</octave></pitch><duration>2</duration><staff>2</staff></note>
                  <note><pitch><step>C</step><octave>2</octave></pitch><duration>2</duration><staff>2</staff></note>
                </measure>
              </part>
            </score-partwise>
            """;

        var measures = MusicXmlImporter.ImportXml(xml, "octave").Project.Parts.Single().Measures;

        string Shifts(int measure, int staff) =>
            string.Join(",", measures[measure].Events.Where(e => e.Kind == "Note" && e.Staff == staff).Select(e => e.OctaveShift));
        Assert.Equal("0,1,1,0", Shifts(0, 0));
        Assert.Equal("0", Shifts(0, 1));
        Assert.Equal("-1", Shifts(1, 1));
        Assert.Equal("-1,0", Shifts(2, 1));
    }

    [Fact]
    public void VoltaSpanningSeveralMeasures_AndUnclosedVolta_AreMeasuredInMeasures()
    {
        const string xml = """
            <score-partwise>
              <part-list><score-part id="P1"><part-name>A</part-name></score-part></part-list>
              <part id="P1">
                <measure number="1"><barline location="left"><ending number="1, 2" type="start"/></barline></measure>
                <measure number="2"/>
                <measure number="3"><barline location="right"><ending number="1, 2" type="stop"/><repeat direction="backward" times="3"/></barline></measure>
                <measure number="4"><barline location="left"><ending number="3" type="start"/></barline></measure>
                <measure number="5"/>
              </part>
            </score-partwise>
            """;

        var result = MusicXmlImporter.ImportXml(xml, "voltas");
        var measures = result.Project.Parts.Single().Measures;

        Assert.Equal(("1, 2", "", 3, false), (measures[0].Volta!.Numbers, measures[0].Volta!.Text, measures[0].Volta!.MeasureCount, measures[0].Volta!.OpenEnd));
        Assert.Equal((true, 3), (measures[2].RepeatEnd, measures[2].RepeatTimes));
        Assert.Equal((2, true), (measures[3].Volta!.MeasureCount, measures[3].Volta!.OpenEnd));
        Assert.Equal("끝나지 않은 볼타(1·2번 괄호)는 마지막 마디까지로 맞춤", Assert.Single(result.Warnings).Message);
    }

    /// <summary>Five parts in Finale's style; <paramref name="chordsInPart1"/> decides whether part 1 has a chord symbol.</summary>
    private static string FinaleStyleParts(bool chordsInPart1) => $"""
        <score-partwise>
          <part-list>
            <score-part id="P1"><part-name print-object="no">MusicXML Part</part-name></score-part>
            <score-part id="P2"><part-name print-object="no">MusicXML Part</part-name></score-part>
            <score-part id="P3"><part-name>Clarinet in Bb</part-name>
              <part-name-display><display-text>Clarinet in B</display-text><accidental-text>flat</accidental-text></part-name-display></score-part>
            <score-part id="P4"><part-name>Voice</part-name></score-part>
            <score-part id="P5"><part-name>Voice</part-name></score-part>
          </part-list>
          <part id="P1"><measure number="1">{(chordsInPart1 ? "<harmony/>" : "")}</measure></part>
          <part id="P2"><measure number="1"/></part>
          <part id="P3"><measure number="1"/></part>
          <part id="P4"><measure number="1"/></part>
          <part id="P5"><measure number="1"><harmony/></measure></part>
        </score-partwise>
        """;

    [Fact]
    public void FinalePlaceholderPartNames_AreNumbered_AndDisplayNamesWin()
    {
        // Finale exports unnamed staves as "MusicXML Part"; part-name-display holds the printed name.
        var result = MusicXmlImporter.ImportXml(FinaleStyleParts(chordsInPart1: true), "finale");

        Assert.Equal(new[] { "파트 1", "파트 2", "Clarinet in B♭", "Voice", "Voice" }, result.Project.Parts.Select(p => p.Name));
        var chordWarning = Assert.Single(result.Warnings);
        Assert.Equal((2, "파트 1 1마디"), (chordWarning.Count, chordWarning.FirstLocation));

        // When names repeat, the location also says which part it is.
        var duplicates = MusicXmlImporter.ImportXml(FinaleStyleParts(chordsInPart1: false), "finale");
        Assert.Equal("5번째 파트(Voice) 1마디", Assert.Single(duplicates.Warnings).FirstLocation);
    }

    public static TheoryData<string, string> InvalidSources => new()
    {
        { "", "비어 있습니다" },
        { "이건 악보가 아닙니다", "XML 형식이 올바르지 않습니다" },
        { "<html><body/></html>", "MusicXML 악보 파일이 아닙니다" },
        { "<score-timewise><part-list/></score-timewise>", "timewise" },
        { "<score-partwise><part-list/></score-partwise>", "가져올 파트가 없습니다" },
        { "<score-partwise><part-list><score-part id='P1'/></part-list><part id='P2'/></score-partwise>", "파트 목록(part-list)에 없는 파트('P2')" },
        { "<score-partwise><part-list><score-part id='P1'/></part-list><part id='P1'><measure>", "XML 형식이 올바르지 않습니다" },
    };

    [Theory]
    [MemberData(nameof(InvalidSources))]
    public void InvalidFiles_ThrowKoreanErrors(string content, string expected)
    {
        var path = Path.Combine(_dir, "bad.musicxml");
        File.WriteAllText(path, content);

        var ex = Assert.Throws<ProjectFileException>(() => MusicXmlImporter.ImportFile(path));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void CorruptMxl_ThrowsKoreanError()
    {
        var path = Path.Combine(_dir, "broken.mxl");
        File.WriteAllBytes(path, new byte[] { (byte)'P', (byte)'K', 3, 4, 1, 2, 3, 4, 5, 6, 7, 8 });

        var ex = Assert.Throws<ProjectFileException>(() => MusicXmlImporter.ImportFile(path));

        Assert.Contains(".mxl", ex.Message);
    }

    [Fact]
    public void ExternalEntities_AreNeverRead()
    {
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "비밀");
        var xml = $"""
            <?xml version="1.0"?>
            <!DOCTYPE score-partwise [ <!ENTITY leak SYSTEM "{new Uri(secret).AbsoluteUri}"> ]>
            <score-partwise>
              <work><work-title>&leak;</work-title></work>
              <part-list><score-part id="P1"><part-name>A</part-name></score-part></part-list>
              <part id="P1"><measure number="1"/></part>
            </score-partwise>
            """;

        // With DTD processing ignored the entity is never expanded, so the parser either rejects it or leaves it empty.
        try
        {
            var result = MusicXmlImporter.ImportXml(xml, "fallback");
            Assert.DoesNotContain("비밀", result.Project.Title);
        }
        catch (ProjectFileException)
        {
        }
    }

    [Fact]
    public void Import_DoesNotChangeTheSourceFile()
    {
        var path = Path.Combine(_dir, "original.musicxml");
        File.Copy(Sample("piano-grand-staff.musicxml"), path);
        var before = File.ReadAllBytes(path);
        var lastWrite = File.GetLastWriteTimeUtc(path);

        MusicXmlImporter.ImportFile(path);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData("simple-melody.musicxml")]
    [InlineData("piano-grand-staff.musicxml")]
    [InlineData("unsupported-features.musicxml")]
    [InlineData("hymn-repeats-chords.musicxml")]
    [InlineData("layout-features.musicxml")]
    [InlineData("beams.musicxml")]
    [InlineData("part-groups.musicxml")]
    [InlineData("page-layout.musicxml")]
    [InlineData("lyrics-ties.musicxml")]
    public void ImportedProject_SurvivesSaveAndReopen(string file)
    {
        var imported = MusicXmlImporter.ImportFile(Sample(file)).Project;
        var path = Path.Combine(_dir, Path.ChangeExtension(file, ".scoreproj"));

        ProjectFileService.Save(path, imported);
        var reopened = ProjectFileService.Load(path);

        Assert.Equal(JsonSerializer.Serialize(imported), JsonSerializer.Serialize(reopened));
        Assert.Equal(Sample(file), reopened.ImportedFrom);
    }
}
