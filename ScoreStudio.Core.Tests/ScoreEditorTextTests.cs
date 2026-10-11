using ScoreStudio.Editing;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Core.Tests;

/// <summary>Lyric and chord symbol entry (docs/SPRINT4G_LYRICS_CHORDS.md).</summary>
public sealed class ScoreEditorTextTests
{
    private static Rational R(long n, long d = 1) => new(n, d);

    private static EditPosition At(int measure, long position, long denominator = 1) => new(0, 0, 1, measure, R(position, denominator));

    /// <summary>A score with the given notes (letters, "r" for a rest) entered as quarters from the start.</summary>
    private static ScoreEditor WithNotes(string letters)
    {
        var editor = new ScoreEditor(ScoreProject.CreateNew());
        foreach (var letter in letters)
            Assert.True(letter == 'r' ? editor.EnterRest().Done : editor.EnterNote(letter).Done);
        return editor;
    }

    /// <summary>Verse 1 syllables in time order as "text:Syllabic".</summary>
    private static List<string> Syllables(ScoreEditor editor, int verse = 1) =>
        editor.Project.Parts[0].Measures.SelectMany(m => m.Events.OrderBy(e => (double)e.PositionNumerator / e.PositionDenominator))
            .SelectMany(e => e.Lyrics.Where(l => l.Verse == verse)).Select(l => $"{l.Text}:{l.Syllabic}").ToList();

    private static List<LineItem> Hyphens(ScoreProject project) =>
        ScoreLayoutEngine.Layout(project).Pages.SelectMany(p => p.Items).OfType<LineItem>()
            .Where(l => l.Y1 == l.Y2 && l.X2 - l.X1 < 2 && Math.Abs(l.Thickness - 0.06 * LyricSize) < 1e-9).ToList();

    /// <summary>The default lyric size: 2.2 staff spaces of the default 1.75 mm.</summary>
    private const double LyricSize = 2.2 * 1.75;

    [Fact]
    public void Syllables_FollowTheHyphenBeforeThem_AndFixTheNextOne()
    {
        var editor = WithNotes("CDEF");
        editor.Select(At(0, 0));
        Assert.True(editor.EnterLyric("Hal", 1, hyphen: true).Done);
        Assert.Equal(At(0, 1), editor.Selection);
        Assert.True(editor.EnterLyric("le", 1, hyphen: true).Done);
        Assert.True(editor.EnterLyric("lu", 1, hyphen: true).Done);
        Assert.True(editor.EnterLyric("jah", 1).Done);
        Assert.Equal(new[] { "Hal:Begin", "le:Middle", "lu:Middle", "jah:End" }, Syllables(editor));
        Assert.Equal(3, Hyphens(editor.Project).Count);

        // The second syllable without its hyphen ends the word; the third then starts a new one.
        editor.Select(At(0, 1));
        Assert.True(editor.SetLyric("le", 1).Done);
        Assert.Equal(new[] { "Hal:Begin", "le:End", "lu:Begin", "jah:End" }, Syllables(editor));

        // An empty syllable removes it; the next one then follows the one before (here: nothing, so a word of its own).
        editor.Select(At(0, 0));
        Assert.True(editor.SetLyric("", 1).Done);
        Assert.Equal(new[] { "le:Single", "lu:Begin", "jah:End" }, Syllables(editor));

        // Removing the last syllable leaves the hyphen typed before it; removing one inside a word keeps the word joined.
        editor.Select(At(0, 3));
        Assert.True(editor.SetLyric("", 1).Done);
        Assert.Equal(new[] { "le:Single", "lu:Begin" }, Syllables(editor));
        var middle = WithNotes("CDE");
        middle.Select(At(0, 0));
        middle.TypeLyrics("Hal-le-lu ", 1);
        middle.Select(At(0, 1));
        Assert.True(middle.SetLyric("", 1).Done);
        Assert.Equal(new[] { "Hal:Begin", "lu:End" }, Syllables(middle));
    }

    [Fact]
    public void KoreanSyllables_AreSingle_AndEntrySkipsRestsAndTiedNotes()
    {
        var editor = WithNotes("CrDE");
        // Tie D (beat 3) into E by making them the same pitch and tied.
        var events = editor.Project.Parts[0].Measures[0].Events.OrderBy(e => (double)e.PositionNumerator / e.PositionDenominator).ToList();
        (events[3].MidiPitch, events[3].NotationSpelling, events[2].TieToNext) = (events[2].MidiPitch, events[2].NotationSpelling, true);
        editor.EnterLetters("F");

        editor.Select(At(0, 0));
        Assert.True(editor.EnterLyric("주", 1).Done);
        Assert.Equal(At(0, 2), editor.Selection); // past the rest
        Assert.True(editor.EnterLyric("님", 1).Done);
        Assert.Equal(At(1, 0), editor.Selection); // past the tied note, into the next measure
        var last = editor.EnterLyric("의", 1);
        Assert.True(last.Done);
        Assert.Contains("뒤에 가사를 붙일 음표가 없습니다", last.Message);
        Assert.Equal(At(1, 0), editor.Selection);
        Assert.Equal(new[] { "주:Single", "님:Single", "의:Single" }, Syllables(editor));
    }

    [Fact]
    public void Lyrics_GoOnNotesOnly_OnTheChordsFirstNote_ByVerse()
    {
        var editor = WithNotes("Cr");
        editor.Select(At(0, 1));
        Assert.False(editor.SetLyric("가", 1).Done); // a rest
        editor.Select(At(0, 0));
        Assert.True(editor.AddToChord('E').Done);
        Assert.True(editor.SetLyric("가", 1).Done);
        Assert.True(editor.SetLyric("나", 2).Done);
        Assert.True(editor.SetLyric("다", 1).Done);

        var chord = editor.Project.Parts[0].Measures[0].Events.Where(e => e.PositionNumerator == 0).ToList();
        Assert.Equal(new[] { "다@1", "나@2" }, chord.SelectMany(e => e.Lyrics).Select(l => $"{l.Text}@{l.Verse}"));
        Assert.Same(chord[0], chord.Single(e => e.Lyrics.Count > 0));
        Assert.False(editor.SetLyric(new string('가', 41), 1).Done);
    }

    [Theory]
    [InlineData("Hal-le", new[] { "Hal-" }, "le")]
    [InlineData("가 나", new[] { "가" }, "나")]
    [InlineData("sing_", new[] { "sing_" }, "")]
    [InlineData("  ", new string[0], "")]
    [InlineData("a b-c_d", new[] { "a", "b-", "c_" }, "d")]
    public void TypedText_IsSplitIntoSyllables(string typed, string[] syllables, string rest)
    {
        var (finished, unfinished) = LyricTyping.Take(typed);
        Assert.Equal(syllables, finished.Select(s => s.Text + (s.Hyphen ? "-" : s.Extend ? "_" : "")));
        Assert.Equal(rest, unfinished);
    }

    [Fact]
    public void TypingLyrics_FillsTheNotesInTurn_AndKeepsTheUnfinishedSyllable()
    {
        var editor = WithNotes("CDEF");
        editor.Select(At(0, 0));
        var (result, rest) = editor.TypeLyrics("Hal-le-lu-jah ", 1);
        Assert.True(result!.Done);
        Assert.Equal("", rest);
        Assert.Equal(new[] { "Hal:Begin", "le:Middle", "lu:Middle", "jah:End" }, Syllables(editor));

        var korean = WithNotes("CDEF");
        korean.Select(At(0, 0));
        Assert.Equal("님", korean.TypeLyrics("주 님", 1).Unfinished);
        Assert.Equal(new[] { "주:Single" }, Syllables(korean));
        Assert.Null(korean.TypeLyrics("님", 1).Result);
    }

    [Theory]
    [InlineData("C", "C", 0, "major", null)]
    [InlineData("Cm", "C", 0, "minor", null)]
    [InlineData("F#m7b5", "F", 1, "half-diminished", null)]
    [InlineData("Bbmaj7/D", "B", -1, "major-seventh", "D")]
    [InlineData("Gsus4", "G", 0, "suspended-fourth", null)]
    [InlineData("E♭7", "E", -1, "dominant", null)]
    [InlineData("Cadd9", "C", 0, "other", null)]
    public void ChordSymbols_AreRead(string typed, string root, int alter, string kind, string? bass)
    {
        var symbol = ChordSymbolText.TryParse(typed, out var error);
        Assert.NotNull(symbol);
        Assert.Equal((root, alter, kind, bass), (symbol.RootStep, symbol.RootAlter, symbol.Kind, symbol.BassStep));
        Assert.Equal("", error);
        Assert.DoesNotContain('♭', symbol.Text);
    }

    [Theory]
    [InlineData("H7")]
    [InlineData("C/X")]
    [InlineData("")]
    [InlineData("C maj")]
    [InlineData("Cmaj7add9add11add13#5")]
    public void BadChordSymbols_AreRefusedInKorean(string typed)
    {
        Assert.Null(ChordSymbolText.TryParse(typed, out var error));
        Assert.Matches("[가-힣]", error);
    }

    [Fact]
    public void ChordSymbols_AreWrittenAtTheSelection_Replaced_Removed_AndMoveOn()
    {
        var editor = WithNotes("CDEF");
        editor.Select(At(0, 0));
        Assert.True(editor.EnterChordSymbol("Bb/D").Done);
        Assert.Equal(At(0, 1), editor.Selection);
        Assert.True(editor.EnterChordSymbol("F").Done);
        Assert.False(editor.EnterChordSymbol("H").Done);

        var texts = ScoreLayoutEngine.Layout(editor.Project).Pages[0].Items.OfType<TextItem>().Select(t => t.Text).ToList();
        Assert.Contains("B♭/D", texts);
        Assert.Contains("F", texts);

        editor.Select(At(0, 0));
        Assert.True(editor.EnterChordSymbol("Gm7").Done);
        Assert.Equal(new[] { "Gm7", "F" }, editor.Project.Parts[0].Measures[0].ChordSymbols.Select(c => c.Text));
        editor.Select(At(0, 1));
        Assert.True(editor.EnterChordSymbol("").Done);
        Assert.False(editor.EnterChordSymbol("").Done); // nothing left there
        Assert.Equal(new[] { "Gm7" }, editor.Project.Parts[0].Measures[0].ChordSymbols.Select(c => c.Text));

        // In an empty measure the symbol goes at the cursor, which then moves to the next measure.
        editor.PlaceCursor(0, 0, 2, R(0));
        Assert.True(editor.EnterChordSymbol("C7").Done);
        Assert.Equal(At(3, 0), editor.Cursor);
        Assert.Single(editor.Project.Parts[0].Measures[2].ChordSymbols);
    }

    [Fact]
    public void EnteredText_SurvivesSaveAndUndo()
    {
        var editor = WithNotes("CDEF");
        var start = ProjectFileService.ToJson(editor.Project);
        editor.Select(At(0, 0));
        editor.TypeLyrics("Glo-ri-a_ ", 1);
        editor.Select(At(0, 0));
        editor.EnterChordSymbol("C");

        var path = Path.Combine(Path.GetTempPath(), $"text-{Guid.NewGuid():N}.scoreproj");
        try
        {
            ProjectFileService.Save(path, editor.Project);
            Assert.Equal(ProjectFileService.ToJson(editor.Project), ProjectFileService.ToJson(ProjectFileService.Load(path)));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(editor.Project.Parts[0].Measures[0].Events.Single(e => e.Lyrics.Any(l => l.Text == "a")).Lyrics[0].Extend);
        for (var i = 0; i < 4; i++) Assert.True(editor.Undo().Done); // three syllables and the chord symbol
        Assert.Equal(start, ProjectFileService.ToJson(editor.Project));
    }
}

internal static class EditorTestExtensions
{
    public static void EnterLetters(this ScoreEditor editor, string letters)
    {
        foreach (var letter in letters) Assert.True(editor.EnterNote(letter).Done);
    }
}
