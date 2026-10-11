using System.Text.Json;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Core.Tests;

public sealed class ProjectFileServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScoreStudioTests", Guid.NewGuid().ToString("N"));

    public ProjectFileServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    private static ScoreProject BuildRichProject()
    {
        var project = ScoreProject.CreateNew();
        project.Title = "주 하나님 지으신 모든 세계 (테스트)";
        project.Composer = "작곡가 이름";
        project.PageSize = "Letter";
        project.Orientation = "Landscape";
        project.ImportedFrom = @"C:\악보\원본.musx";
        project.Parts = new List<ScorePart>
        {
            new()
            {
                Id = "part-s", Name = "소프라노", Instrument = "Voice",
                Measures =
                {
                    new()
                    {
                        Number = 1, Numerator = 6, Denominator = 8, KeyFifths = -3, RepeatStart = true, RepeatEnd = true, RepeatTimes = 3,
                        Volta = new ScoreVolta { Numbers = "1, 2", Text = "1.-2.", MeasureCount = 2, OpenEnd = true },
                        ChordSymbols =
                        {
                            new() { RootStep = "B", RootAlter = -1, Kind = "major-seventh", BassStep = "D", Text = "Bbmaj7/D", PositionNumerator = 3, PositionDenominator = 2 },
                        },
                        Events =
                        {
                            new() { Kind = "Note", MidiPitch = 67, DurationNumerator = 3, DurationDenominator = 2, PositionNumerator = 0, PositionDenominator = 1, NotationSpelling = "G4", TieToNext = true, Lyrics = { new() { Verse = 1, Text = "주", Syllabic = LyricSyllabic.Begin }, new() { Verse = 2, Text = "나", Extend = true } } },
                            new() { Kind = "Rest", MidiPitch = 60, DurationNumerator = 1, DurationDenominator = 2, PositionNumerator = 3, PositionDenominator = 2 },
                            new() { Kind = "Note", MidiPitch = 70, DurationNumerator = 1, DurationDenominator = 3, PositionNumerator = 2, PositionDenominator = 1, NotationSpelling = "Bb4", Lyrics = { new() { Verse = 1, Text = "하" } } },
                        }
                    },
                    new() { Number = 2, Numerator = 6, Denominator = 8 },
                }
            },
            new() { Id = "part-p", Name = "피아노", Instrument = "Piano" },
            new() { Id = "part-v", Name = "바이올린", Instrument = "Violin" },
        };
        return project;
    }

    [Fact]
    public void SaveThenLoad_PreservesTitlePartsAndScoreData()
    {
        var original = BuildRichProject();
        var path = PathFor("rich.scoreproj");

        ProjectFileService.Save(path, original);
        var loaded = ProjectFileService.Load(path);

        Assert.Equal(original.Title, loaded.Title);
        Assert.Equal(original.Composer, loaded.Composer);
        Assert.Equal(original.PageSize, loaded.PageSize);
        Assert.Equal(original.Orientation, loaded.Orientation);
        Assert.Equal(original.ImportedFrom, loaded.ImportedFrom);
        Assert.Equal(original.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(original.ModifiedUtc, loaded.ModifiedUtc);

        // Compare the whole object graph, so a property added later is covered automatically.
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(loaded));

        Assert.Equal(new[] { "소프라노", "피아노", "바이올린" }, loaded.Parts.Select(p => p.Name));
        var e = loaded.Parts[0].Measures[0].Events[2];
        Assert.Equal((70, 1, 3, 2, 1, "Bb4", "하"),
            (e.MidiPitch, e.DurationNumerator, e.DurationDenominator, e.PositionNumerator, e.PositionDenominator, e.NotationSpelling, e.Lyrics.Single().Text));
    }

    [Fact]
    public void SaveThenLoad_NewProject_KeepsDefaultPart()
    {
        var path = PathFor("new.scoreproj");
        ProjectFileService.Save(path, ScoreProject.CreateNew());

        var loaded = ProjectFileService.Load(path);

        Assert.Equal("새 악보", loaded.Title);
        Assert.Single(loaded.Parts);
        Assert.Equal("파트 1", loaded.Parts[0].Name);
        Assert.Equal(ScoreProject.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Save_OverwritesExistingFile_AndLeavesNoTempFiles()
    {
        var path = PathFor("overwrite.scoreproj");
        var project = ScoreProject.CreateNew();
        ProjectFileService.Save(path, project);

        project.Title = "두 번째 제목";
        ProjectFileService.Save(path, project);

        Assert.Equal("두 번째 제목", ProjectFileService.Load(path).Title);
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_WritesUtf8WithoutBom_AndReadableKorean()
    {
        var path = PathFor("utf8.scoreproj");
        ProjectFileService.Save(path, BuildRichProject());

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains($"\"SchemaVersion\": {ScoreProject.CurrentSchemaVersion}", File.ReadAllText(path));
    }

    [Fact]
    public void Load_SampleFile_ReadsAllParts()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "sample-choir.scoreproj");

        var project = ProjectFileService.Load(path);

        Assert.Equal("샘플 합창곡", project.Title);
        Assert.Equal(new[] { "소프라노", "피아노" }, project.Parts.Select(p => p.Name));
        Assert.Equal(4, project.Parts[0].Measures[0].Events.Count);
        Assert.Equal("Rest", project.Parts[0].Measures[0].Events[3].Kind);
        Assert.Equal((3, 4), (project.Parts[1].Measures[0].Numerator, project.Parts[1].Measures[0].Denominator));
        Assert.Equal(new[] { ClefType.Treble, ClefType.Bass }, project.Parts[1].Staves.Select(s => s.Clef));
        Assert.Equal(32, project.Parts[0].Measures.Count);
    }

    [Fact]
    public void Load_Version1File_GetsOneTrebleStaffPerPart_AndSavesAsCurrentVersion()
    {
        var project = ProjectFileService.Parse("""
            { "SchemaVersion": 1, "Title": "옛 파일", "Parts": [ { "Name": "A" }, { "Name": "B", "Measures": [ { "Number": 1 } ] } ] }
            """);

        Assert.All(project.Parts, part => Assert.Equal(ClefType.Treble, Assert.Single(part.Staves).Clef));

        var path = PathFor("upgraded.scoreproj");
        ProjectFileService.Save(path, project);
        Assert.Equal(ScoreProject.CurrentSchemaVersion, ProjectFileService.Load(path).SchemaVersion);
        Assert.Contains("\"Clef\": \"Treble\"", File.ReadAllText(path));
    }

    [Fact]
    public void Load_Version3File_MovesTheSingleLyricIntoVerseOne_AndSavesWithoutIt()
    {
        var project = ProjectFileService.Parse("""
            { "SchemaVersion": 3, "Parts": [ { "Measures": [ { "Events": [ { "Kind": "Note", "Lyric": "옛" }, { "Kind": "Note" } ] } ] } ] }
            """);

        var events = project.Parts[0].Measures[0].Events;
        var lyric = Assert.Single(events[0].Lyrics);
        Assert.Equal((1, "옛", LyricSyllabic.Single, false), (lyric.Verse, lyric.Text, lyric.Syllabic, lyric.Extend));
        Assert.Empty(events[1].Lyrics);
        Assert.Null(events[0].LegacyLyric);

        var path = PathFor("v3.scoreproj");
        ProjectFileService.Save(path, project);
        Assert.DoesNotContain("\"Lyric\"", File.ReadAllText(path));
        Assert.Equal("옛", ProjectFileService.Load(path).Parts[0].Measures[0].Events[0].Lyrics.Single().Text);
    }

    [Fact]
    public void Load_Version2File_GetsDefaultKeyStaffAndVoice()
    {
        var project = ProjectFileService.Parse("""
            { "SchemaVersion": 2, "Parts": [ { "Staves": [ { "Clef": "Treble" } ], "Measures": [ { "Events": [ { "Kind": "Note" } ] } ] } ] }
            """);

        var measure = project.Parts[0].Measures[0];
        Assert.Equal((0, 0, 1), (measure.KeyFifths, measure.Events[0].Staff, measure.Events[0].Voice));
    }

    [Fact]
    public void Load_Version4File_HasNoClefChangesHiddenStavesBreaksOctaveLinesOrBeams()
    {
        var project = ProjectFileService.Parse("""
            { "SchemaVersion": 4, "Parts": [ { "Measures": [ { "Events": [ { "Kind": "Note", "MidiPitch": 72 } ] } ] } ] }
            """);

        var measure = project.Parts[0].Measures[0];
        Assert.Empty(measure.ClefChanges);
        Assert.Empty(measure.HiddenStaves);
        Assert.False(measure.SystemBreak);
        Assert.False(measure.PageBreak);
        Assert.Equal(0, measure.Events[0].OctaveShift);
        Assert.Empty(measure.Events[0].Beams);
    }

    [Fact]
    public void SaveThenLoad_PreservesClefChangesHiddenStavesBreaksOctaveLinesAndBeams()
    {
        var project = ScoreProject.CreateNew();
        project.Parts[0].Staves = new List<ScoreStaff> { new() { Clef = ClefType.Treble }, new() { Clef = ClefType.Bass } };
        var measure = project.Parts[0].Measures[1];
        measure.ClefChanges.Add(new ScoreClefChange { Staff = 1, PositionNumerator = 3, PositionDenominator = 2, Clef = ClefType.Treble });
        measure.HiddenStaves.Add(0);
        measure.SystemBreak = true;
        measure.PageBreak = true;
        measure.Events.Add(new ScoreEvent { Kind = "Note", MidiPitch = 84, NotationSpelling = "C6", OctaveShift = 1, Beams = { BeamType.Begin, BeamType.ForwardHook } });
        measure.Events.Add(new ScoreEvent { Kind = "Note", MidiPitch = 31, NotationSpelling = "G1", OctaveShift = -2, Staff = 1 });
        var path = PathFor("layout.scoreproj");

        ProjectFileService.Save(path, project);
        var loaded = ProjectFileService.Load(path).Parts[0].Measures[1];

        var change = Assert.Single(loaded.ClefChanges);
        Assert.Equal((1, 3, 2, ClefType.Treble), (change.Staff, change.PositionNumerator, change.PositionDenominator, change.Clef));
        Assert.Equal(new[] { 0 }, loaded.HiddenStaves);
        Assert.True(loaded.SystemBreak && loaded.PageBreak);
        Assert.Equal(new[] { 1, -2 }, loaded.Events.Select(e => e.OctaveShift));
        Assert.Equal(new[] { BeamType.Begin, BeamType.ForwardHook }, loaded.Events[0].Beams);
        Assert.Empty(loaded.Events[1].Beams);
        Assert.False(ProjectFileService.Load(path).Parts[0].Measures[0].SystemBreak);
    }

    [Fact]
    public void Load_Version6File_ShowsAllPartNames_AndHasNoPartGroups()
    {
        var project = ProjectFileService.Parse("""{ "SchemaVersion": 6, "Parts": [ { "Name": "A" } ] }""");

        Assert.True(project.Parts[0].ShowName);
        Assert.Empty(project.PartGroups);
    }

    [Fact]
    public void Load_Version7File_HasNoSourceLayout()
    {
        var project = ProjectFileService.Parse("""{ "SchemaVersion": 7, "Parts": [ { "Measures": [ {} ] } ] }""");

        Assert.Null(project.Engraving);
        var measure = project.Parts[0].Measures[0];
        Assert.Equal((null, null), (measure.SystemDistance, measure.TopSystemDistance));
        Assert.Empty(measure.StaffDistances);
    }

    [Fact]
    public void SaveThenLoad_PreservesSourceLayout()
    {
        var project = ScoreProject.CreateNew();
        project.Engraving = new ScoreEngraving { StaffSpace = 1.98, MarginLeft = 20, MarginRight = 21, MarginTop = 22, MarginBottom = 23, SystemDistance = 7.2, TopSystemDistance = 3.3, StaffDistance = 8, LyricFontSize = 4 };
        project.Parts[0].Measures[0].Events.Add(new ScoreEvent { Lyrics = { new ScoreLyric { Text = "가", Y = -9.2 } } });
        project.Parts[0].Measures[0].ChordSymbols.Add(new ScoreChordSymbol { RootStep = "F", Text = "F", Y = 2.6 });
        var measure = project.Parts[0].Measures[1];
        (measure.SystemDistance, measure.TopSystemDistance) = (17.4, 31.3);
        measure.StaffDistances.Add(new ScoreStaffDistance { Staff = 0, Distance = 14.5 });
        var path = PathFor("layout7.scoreproj");

        ProjectFileService.Save(path, project);
        var loaded = ProjectFileService.Load(path);

        var e = loaded.Engraving!;
        Assert.Equal((1.98, 20.0, 21.0, 22.0, 23.0, 7.2, 3.3, 8.0),
            (e.StaffSpace, e.MarginLeft, e.MarginRight, e.MarginTop, e.MarginBottom, e.SystemDistance, e.TopSystemDistance, e.StaffDistance));
        var m = loaded.Parts[0].Measures[1];
        Assert.Equal((17.4, 31.3, 14.5), (m.SystemDistance, m.TopSystemDistance, m.StaffDistances.Single().Distance));
        Assert.Equal(4, e.LyricFontSize);
        Assert.Equal(-9.2, loaded.Parts[0].Measures[0].Events[0].Lyrics.Single().Y);
        Assert.Equal(2.6, loaded.Parts[0].Measures[0].ChordSymbols.Single().Y);
    }

    [Fact]
    public void SaveThenLoad_PreservesPartGroupsAndHiddenNames()
    {
        var project = ScoreProject.CreateNew();
        project.Parts.Add(new ScorePart { Name = "피아노", ShowName = false });
        project.PartGroups.Add(new ScorePartGroup { FirstPart = 0, LastPart = 1, Symbol = PartGroupSymbol.Brace, JoinBarlines = true });
        var path = PathFor("groups.scoreproj");

        ProjectFileService.Save(path, project);
        var loaded = ProjectFileService.Load(path);

        Assert.Equal(new[] { true, false }, loaded.Parts.Select(p => p.ShowName));
        var group = Assert.Single(loaded.PartGroups);
        Assert.Equal((0, 1, PartGroupSymbol.Brace, true), (group.FirstPart, group.LastPart, group.Symbol, group.JoinBarlines));
    }

    [Fact]
    public void SaveThenLoad_PreservesStavesAndClefs()
    {
        var project = ScoreProject.CreateNew();
        project.Parts.Add(new ScorePart
        {
            Name = "피아노",
            Staves = new List<ScoreStaff> { new() { Clef = ClefType.Treble }, new() { Clef = ClefType.Bass } },
        });
        project.Parts[0].Staves = new List<ScoreStaff> { new() { Clef = ClefType.Treble8vb } };
        project.Parts.Add(new ScorePart { Name = "비올라", Staves = new List<ScoreStaff> { new() { Clef = ClefType.Alto } } });
        project.Parts.Add(new ScorePart { Name = "첼로", Staves = new List<ScoreStaff> { new() { Clef = ClefType.Tenor } } });
        var path = PathFor("staves.scoreproj");

        ProjectFileService.Save(path, project);
        var loaded = ProjectFileService.Load(path);

        Assert.Equal(
            new[] { "Treble8vb", "Treble,Bass", "Alto", "Tenor" },
            loaded.Parts.Select(p => string.Join(",", p.Staves.Select(s => s.Clef))));
    }

    [Fact]
    public void CreateNew_HasFourEmptyMeasuresInFourFour()
    {
        var measures = ScoreProject.CreateNew().Parts.Single().Measures;

        Assert.Equal(new[] { 1, 2, 3, 4 }, measures.Select(m => m.Number));
        Assert.All(measures, m => Assert.Equal((4, 4, 0), (m.Numerator, m.Denominator, m.Events.Count)));
    }

    [Fact]
    public void Load_MissingNestedLists_AreFilledWithEmptyLists()
    {
        var project = ProjectFileService.Parse("""
            { "SchemaVersion": 1, "Title": null, "Parts": [ { "Name": "A", "Measures": [ { "Number": 1, "Events": null } ] }, { "Name": "B", "Measures": null } ] }
            """);

        Assert.Equal("", project.Title);
        Assert.Empty(project.Parts[0].Measures[0].Events);
        Assert.Empty(project.Parts[1].Measures);
        Assert.False(string.IsNullOrEmpty(project.Parts[1].Id));
    }

    [Fact]
    public void Load_SchemaVersionIsCaseInsensitive()
    {
        var project = ProjectFileService.Parse("""{ "schemaVersion": 1, "title": "소문자 키" }""");
        Assert.Equal("소문자 키", project.Title);
    }

    public static TheoryData<string, string> InvalidFiles => new()
    {
        { "", "비어 있습니다" },
        { "   \n  ", "비어 있습니다" },
        { "이건 JSON이 아닙니다", "손상되었거나 형식이 올바르지 않습니다" },
        { """{ "SchemaVersion": 1, "Title": "끊긴 파일", "Parts": [ """, "손상되었거나 형식이 올바르지 않습니다" },
        { "null", "프로젝트 파일이 아닙니다" },
        { "[1, 2, 3]", "프로젝트 파일이 아닙니다" },
        { """{ "Title": "버전 없음" }""", "버전 정보(SchemaVersion)가 없습니다" },
        { """{ "SchemaVersion": "1" }""", "버전 정보(SchemaVersion)가 올바르지 않습니다" },
        { """{ "SchemaVersion": 99 }""", "더 새로운 버전" },
        { """{ "SchemaVersion": 0 }""", "지원하지 않는 프로젝트 버전" },
        { """{ "SchemaVersion": 1, "Parts": "파트" }""", "손상되었거나 형식이 올바르지 않습니다" },
        { """{ "SchemaVersion": 1, "Parts": [ null ] }""", "1번째 파트 데이터가 비어 있습니다" },
        { """{ "SchemaVersion": 1, "Parts": [ { "Measures": [ { "Numerator": 4, "Denominator": 0 } ] } ] }""", "박자표(4/0)" },
        { """{ "SchemaVersion": 1, "Parts": [ { "Measures": [ { "Events": [ { "DurationDenominator": 0 } ] } ] } ] }""", "음표/쉼표 길이" },
        { """{ "SchemaVersion": 1, "Parts": [ { "Measures": [ { "Events": [ { "PositionNumerator": -1 } ] } ] } ] }""", "음표/쉼표 위치" },
        { """{ "SchemaVersion": 1, "Parts": [ { "Measures": [ { "Events": [ { "MidiPitch": 200 } ] } ] } ] }""", "음높이(200)" },
        { """{ "SchemaVersion": 2, "Parts": [ { "Staves": [ null ] } ] }""", "1번째 보표 데이터가 비어 있습니다" },
        { """{ "SchemaVersion": 3, "Parts": [ { "Measures": [ { "KeyFifths": 8 } ] } ] }""", "조표(8)" },
        { """{ "SchemaVersion": 3, "Parts": [ { "Measures": [ { "Events": [ { "Staff": 1 } ] } ] } ] }""", "없는 보표(2번째)" },
        { """{ "SchemaVersion": 3, "Parts": [ { "Measures": [ { "Events": [ { "Voice": 0 } ] } ] } ] }""", "성부 번호(0)" },
        { """{ "SchemaVersion": 4, "Parts": [ { "Measures": [ { "RepeatTimes": 1 } ] } ] }""", "반복 횟수(1)" },
        { """{ "SchemaVersion": 4, "Parts": [ { "Measures": [ { "Volta": { "Numbers": "", "MeasureCount": 1 } } ] } ] }""", "볼타" },
        { """{ "SchemaVersion": 4, "Parts": [ { "Measures": [ { "Volta": { "Numbers": "1", "MeasureCount": 0 } } ] } ] }""", "볼타" },
        { """{ "SchemaVersion": 4, "Parts": [ { "Measures": [ { "ChordSymbols": [ { "RootStep": "H" } ] } ] } ] }""", "1번째 코드 이름" },
        { """{ "SchemaVersion": 4, "Parts": [ { "Measures": [ { "ChordSymbols": [ null ] } ] } ] }""", "1번째 코드 이름 데이터가 비어 있습니다" },
        { """{ "SchemaVersion": 4, "Parts": [ { "Measures": [ { "Events": [ { "Lyrics": [ { "Verse": 0, "Text": "가" } ] } ] } ] } ] }""", "가사 정보" },
        { """{ "SchemaVersion": 4, "Parts": [ { "Measures": [ { "Events": [ { "Lyrics": [ { "Syllabic": "Hyphen" } ] } ] } ] } ] }""", "손상되었거나 형식이 올바르지 않습니다" },
        { """{ "SchemaVersion": 5, "Parts": [ { "Measures": [ { "ClefChanges": [ { "Staff": 1, "Clef": "Bass" } ] } ] } ] }""", "음자리표 변경" },
        { """{ "SchemaVersion": 5, "Parts": [ { "Measures": [ { "ClefChanges": [ { "PositionDenominator": 0 } ] } ] } ] }""", "음자리표 변경" },
        { """{ "SchemaVersion": 5, "Parts": [ { "Measures": [ { "ClefChanges": [ null ] } ] } ] }""", "음자리표 변경" },
        { """{ "SchemaVersion": 5, "Parts": [ { "Measures": [ { "HiddenStaves": [ 1 ] } ] } ] }""", "숨긴 보표" },
        { """{ "SchemaVersion": 5, "Parts": [ { "Measures": [ { "Events": [ { "OctaveShift": 3 } ] } ] } ] }""", "옥타브 기호 값(3)" },
        { """{ "SchemaVersion": 6, "Parts": [ { "Measures": [ { "Events": [ { "Beams": [ "Begin", "Begin", "Begin", "Begin", "Begin", "Begin", "Begin" ] } ] } ] } ] }""", "빔 정보" },
        { """{ "SchemaVersion": 6, "Parts": [ { "Measures": [ { "Events": [ { "Beams": [ "Sideways" ] } ] } ] } ] }""", "손상되었거나 형식이 올바르지 않습니다" },
        { """{ "SchemaVersion": 7, "Parts": [ {} ], "PartGroups": [ { "FirstPart": 0, "LastPart": 1 } ] }""", "1번째 파트 묶음" },
        { """{ "SchemaVersion": 7, "Parts": [ {}, {} ], "PartGroups": [ { "FirstPart": 1, "LastPart": 0 } ] }""", "1번째 파트 묶음" },
        { """{ "SchemaVersion": 7, "Parts": [ {} ], "PartGroups": [ null ] }""", "1번째 파트 묶음" },
        { """{ "SchemaVersion": 7, "Parts": [ {} ], "PartGroups": [ { "Symbol": "Curly" } ] }""", "손상되었거나 형식이 올바르지 않습니다" },
        { """{ "SchemaVersion": 8, "Engraving": { "StaffSpace": 0 } }""", "페이지 배치 정보" },
        { """{ "SchemaVersion": 8, "Engraving": { "MarginTop": -1 } }""", "페이지 배치 정보" },
        { """{ "SchemaVersion": 8, "Engraving": { "SystemDistance": 500 } }""", "페이지 배치 정보" },
        { """{ "SchemaVersion": 8, "Parts": [ { "Measures": [ { "SystemDistance": -3 } ] } ] }""", "줄·보표 간격" },
        { """{ "SchemaVersion": 8, "Parts": [ { "Measures": [ { "StaffDistances": [ { "Staff": 1, "Distance": 9 } ] } ] } ] }""", "줄·보표 간격" },
        { """{ "SchemaVersion": 9, "Engraving": { "LyricFontSize": 0.2 } }""", "페이지 배치 정보" },
        { """{ "SchemaVersion": 9, "Parts": [ { "Measures": [ { "Events": [ { "Lyrics": [ { "Text": "가", "Y": 900 } ] } ] } ] } ] }""", "가사 정보" },
        { """{ "SchemaVersion": 10, "Parts": [ { "Measures": [ { "ChordSymbols": [ { "RootStep": "C", "Y": -500 } ] } ] } ] }""", "1번째 코드 이름" },
        { """{ "SchemaVersion": 2, "Parts": [ { "Staves": [ { "Clef": "Percussion" } ] } ] }""", "손상되었거나 형식이 올바르지 않습니다" },
        { """{ "SchemaVersion": 2, "Parts": [ { "Staves": [ { "Clef": 7 } ] } ] }""", "손상되었거나 형식이 올바르지 않습니다" },
    };

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Load_InvalidFile_ThrowsProjectFileExceptionWithKoreanMessage(string content, string expectedMessagePart)
    {
        var path = PathFor("invalid.scoreproj");
        File.WriteAllText(path, content);

        var ex = Assert.Throws<ProjectFileException>(() => ProjectFileService.Load(path));

        Assert.Contains(expectedMessagePart, ex.Message);
    }

    [Fact]
    public void Load_TextFile_ShowsOnlyKoreanMessage_AndKeepsParserError()
    {
        // A Korean note saved from Notepad as .scoreproj: what a user is most likely to open by mistake.
        var path = PathFor("메모.scoreproj");
        File.WriteAllText(path, "한글 메모입니다");

        var ex = Assert.Throws<ProjectFileException>(() => ProjectFileService.Load(path));

        Assert.Equal("프로젝트 파일이 손상되었거나 형식이 올바르지 않습니다 (1번째 줄 부근).", ex.Message);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Fact]
    public void Load_BinaryGarbage_ThrowsProjectFileException()
    {
        var path = PathFor("garbage.scoreproj");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF, 0xFE, 0x10 });

        Assert.Throws<ProjectFileException>(() => ProjectFileService.Load(path));
    }

    [Fact]
    public void Load_MissingFile_ThrowsProjectFileException()
    {
        var ex = Assert.Throws<ProjectFileException>(() => ProjectFileService.Load(PathFor("없는 파일.scoreproj")));
        Assert.Contains("파일을 찾을 수 없습니다", ex.Message);
    }

    [Fact]
    public void Load_InvalidFile_DoesNotModifyIt()
    {
        var path = PathFor("keep.scoreproj");
        const string content = "{ 손상된 내용";
        File.WriteAllText(path, content);
        var before = File.GetLastWriteTimeUtc(path);

        Assert.Throws<ProjectFileException>(() => ProjectFileService.Load(path));

        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }
}
