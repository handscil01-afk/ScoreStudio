using ScoreStudio.Editing;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Core.Tests;

/// <summary>Tuplet entry (docs/SPRINT4F_TUPLETS.md, entry).</summary>
public sealed class ScoreEditorTupletTests
{
    private static Rational R(long n, long d = 1) => new(n, d);

    private static ScoreEditor NewEditor() => new(ScoreProject.CreateNew());

    private static void Enter(ScoreEditor editor, string letters)
    {
        foreach (var letter in letters)
        {
            var result = letter == 'r' ? editor.EnterRest() : editor.EnterNote(letter);
            Assert.True(result.Done, $"entering {letter}: {result.Message}");
        }
    }

    private static ScoreMeasure Measure(ScoreEditor editor, int index = 0) => editor.Project.Parts[0].Measures[index];

    /// <summary>Voice 1 of a measure as "start+length kind" in time order, e.g. "1/3+1/3 Note".</summary>
    private static List<string> Voice(ScoreEditor editor, int measure = 0) =>
        Measure(editor, measure).Events.Where(e => e.Voice == 1)
            .GroupBy(e => new Rational(e.PositionNumerator, e.PositionDenominator)).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}+{new Rational(g.First().DurationNumerator, g.First().DurationDenominator)} {g.First().Kind}").ToList();

    private static List<string> Tuplets(ScoreEditor editor, int measure = 0) =>
        Measure(editor, measure).Tuplets.OrderBy(t => new Rational(t.PositionNumerator, t.PositionDenominator))
            .Select(t => $"{new Rational(t.PositionNumerator, t.PositionDenominator)}+{new Rational(t.DurationNumerator, t.DurationDenominator)} {t.Actual}:{t.Normal}").ToList();

    private static int TupletGlyphs(ScoreProject project, char glyph) =>
        ScoreLayoutEngine.Layout(project).Pages.SelectMany(p => p.Items).OfType<GlyphItem>().Count(g => g.Glyph == glyph);

    [Fact]
    public void ATriplet_OfEighths_TakesOneBeat_IsBeamed_AndEntryGoesBackToNormalAfterIt()
    {
        var editor = NewEditor();
        editor.SetInputValue(NoteType.Eighth, 0);
        Assert.True(editor.StartTuplet(3).Done);
        Assert.Equal(3, editor.PendingTuplet);
        Enter(editor, "CDE");

        Assert.Null(editor.PendingTuplet);
        Assert.Equal(new[] { "0+1/3 Note", "1/3+1/3 Note", "2/3+1/3 Note" }, Voice(editor));
        Assert.Equal(new[] { "0+1 3:2" }, Tuplets(editor));
        Assert.Equal("Begin,Continue,End", string.Join(",", Measure(editor).Events.Select(e => e.Beams.Single())));
        Assert.Equal(new EditPosition(0, 0, 1, 0, R(1)), editor.Cursor);

        // After the tuplet, eighths are plain again.
        Enter(editor, "F");
        Assert.Equal("1+1/2 Note", Voice(editor)[3]);
        Assert.Equal(1, TupletGlyphs(editor.Project, (char)(Smufl.Tuplet0 + 3)));
        Assert.Contains("셋잇단음표", editor.StartTuplet(3).Message);
    }

    [Fact]
    public void TheFirstNoteAlone_LeavesTupletRests_AndTheNextNotesGoOverThem()
    {
        var editor = NewEditor();
        editor.SetInputValue(NoteType.Eighth, 0);
        editor.StartTuplet(3);
        var result = editor.EnterNote('C');
        Assert.Contains("셋잇단음표", result.Message);

        // The rest of the triplet is a rest written as a quarter in the triplet.
        Assert.Equal(new[] { "0+1/3 Note", "1/3+2/3 Rest" }, Voice(editor));
        Enter(editor, "r");
        Assert.Equal(new[] { "0+1/3 Note", "1/3+1/3 Rest", "2/3+1/3 Rest" }, Voice(editor));

        // A value longer than what is left of the triplet is refused.
        editor.SetInputValue(NoteType.Quarter, 0);
        var refused = editor.EnterNote('D');
        Assert.False(refused.Done);
        Assert.Contains("셋잇단음표에 남은 길이", refused.Message);
    }

    [Fact]
    public void QuarterTriplets_TakeTwoBeats_AndSextupletSixteenthsOneBeam()
    {
        var editor = NewEditor();
        editor.StartTuplet(3);
        Enter(editor, "CDE");
        Assert.Equal(new[] { "0+2/3 Note", "2/3+2/3 Note", "4/3+2/3 Note" }, Voice(editor));
        Assert.Equal(new[] { "0+2 3:2" }, Tuplets(editor));

        editor.SetInputValue(NoteType.Sixteenth, 0);
        editor.StartTuplet(6);
        Enter(editor, "FGABCD");
        Assert.Equal(new[] { "0+2 3:2", "2+1 6:4" }, Tuplets(editor));
        var sextuplet = Measure(editor).Events.Where(e => new Rational(e.PositionNumerator, e.PositionDenominator) >= R(2)).ToList();
        Assert.All(sextuplet, e => Assert.Equal(R(1, 6), new Rational(e.DurationNumerator, e.DurationDenominator)));
        Assert.Equal("Begin;Continue;Continue;Continue;Continue;End", string.Join(";", sextuplet.Select(e => e.Beams[0])));
        Assert.All(sextuplet, e => Assert.Equal(2, e.Beams.Count));

        // A triplet across the beat (starting half a beat in) is still beamed as one group, by the tuplet.
        var offBeat = NewEditor();
        offBeat.SetInputValue(NoteType.Eighth, 0);
        Enter(offBeat, "C");
        offBeat.StartTuplet(3);
        Enter(offBeat, "DEF");
        Assert.Equal(new[] { "1/2+1 3:2" }, Tuplets(offBeat));
        var triplet = Measure(offBeat).Events.Where(e => e.PositionNumerator != 0).OrderBy(e => (double)e.PositionNumerator / e.PositionDenominator).ToList();
        Assert.Equal("Begin;Continue;End", string.Join(";", triplet.Select(e => e.Beams.Single())));
        Assert.Empty(Measure(offBeat).Events.Single(e => e.PositionNumerator == 0).Beams);

        // A tuplet longer than what is left of the measure is refused.
        editor.SetInputValue(NoteType.Quarter, 0);
        editor.PlaceCursor(0, 0, 0, R(3));
        editor.StartTuplet(3);
        Assert.False(editor.EnterNote('E').Done);
    }

    [Fact]
    public void ANestedTriplet_IsShortenedByBoth()
    {
        var editor = NewEditor();
        editor.StartTuplet(3);
        Enter(editor, "C");
        editor.SetInputValue(NoteType.Eighth, 0);
        editor.StartTuplet(3);
        Enter(editor, "DEF");
        editor.SetInputValue(NoteType.Quarter, 0);
        Enter(editor, "G");

        Assert.Equal(new[] { "0+2/3 Note", "2/3+2/9 Note", "8/9+2/9 Note", "10/9+2/9 Note", "4/3+2/3 Note" }, Voice(editor));
        Assert.Equal(new[] { "0+2 3:2", "2/3+2/3 3:2" }, Tuplets(editor));
        Assert.Equal(2, TupletGlyphs(editor.Project, (char)(Smufl.Tuplet0 + 3)));
    }

    [Fact]
    public void OverwritingPartOfATuplet_FromOutside_RemovesIt_AndDeletingItsNotesToo()
    {
        var editor = NewEditor();
        editor.SetInputValue(NoteType.Eighth, 0);
        editor.PlaceCursor(0, 0, 0, R(1));
        editor.StartTuplet(3);
        Enter(editor, "CDE");
        Assert.Equal(new[] { "1+1 3:2" }, Tuplets(editor));

        // A quarter from beat 1½ covers the first triplet note: the triplet goes, the rest of its time is a plain rest.
        editor.SetInputValue(NoteType.Quarter, 0);
        editor.PlaceCursor(0, 0, 0, R(1, 2));
        Enter(editor, "F");
        Assert.Empty(Tuplets(editor));
        Assert.Equal(new[] { "1/2+1 Note", "3/2+1/2 Rest" }, Voice(editor));

        // Deleting every note of a triplet removes it; its time becomes plain rests.
        var other = NewEditor();
        other.SetInputValue(NoteType.Eighth, 0);
        other.StartTuplet(3);
        Enter(other, "CDE");
        foreach (var position in new[] { R(0), R(1, 3), R(2, 3) })
        {
            Assert.True(other.Select(new EditPosition(0, 0, 1, 0, position)).Done);
            Assert.True(other.DeleteSelection().Done);
            Assert.Equal(position == R(2, 3) ? 0 : 1, Measure(other).Tuplets.Count);
        }

        Assert.Equal(new[] { "0+1 Rest" }, Voice(other));
        Assert.Null(other.Selection);
    }

    [Fact]
    public void LengthChanges_InATuplet_UseItsValues_AndStayInIt()
    {
        var editor = NewEditor();
        editor.SetInputValue(NoteType.Eighth, 0);
        editor.StartTuplet(3);
        Enter(editor, "C");
        Assert.True(editor.Select(new EditPosition(0, 0, 1, 0, R(0))).Done);

        Assert.True(editor.SetInputValue(NoteType.Quarter, 0).Done);
        Assert.Equal(new[] { "0+2/3 Note", "2/3+1/3 Rest" }, Voice(editor));
        var refused = editor.SetInputValue(NoteType.Half, 0);
        Assert.False(refused.Done);
        Assert.Contains("셋잇단음표 끝을 넘어", refused.Message);
        Assert.True(editor.SetInputValue(NoteType.Eighth, 0).Done);
        Assert.Equal(new[] { "0+1/3 Note", "1/3+1/3 Rest", "2/3+1/3 Rest" }, Voice(editor));
    }

    [Fact]
    public void ClickingInATuplet_EntersItsValue_AndCopyPasteAndUndoKeepTuplets()
    {
        var editor = NewEditor();
        var initial = ProjectFileService.ToJson(editor.Project);
        editor.SetInputValue(NoteType.Eighth, 0);
        editor.StartTuplet(3);
        Enter(editor, "C");

        // A click on the triplet's second eighth (diatonic 30 = E4 in C major).
        Assert.True(editor.EnterNoteAt(new EditPosition(0, 0, 1, 0, R(1, 3)), 30).Done);
        Assert.Equal(new[] { "0+1/3 Note", "1/3+1/3 Note", "2/3+1/3 Rest" }, Voice(editor));

        editor.PlaceCursor(0, 0, 0, R(0));
        Assert.True(editor.CopyMeasure().Done);
        editor.PlaceCursor(0, 0, 2, R(0));
        Assert.True(editor.PasteMeasure().Done);
        Assert.Equal(Tuplets(editor, 0), Tuplets(editor, 2));
        Assert.Equal(Voice(editor, 0), Voice(editor, 2));

        // A shorter time signature may not cut the tuplet.
        editor.PlaceCursor(0, 0, 0, R(0));
        Assert.False(editor.SetTime(1, 8).Done);

        var path = Path.Combine(Path.GetTempPath(), $"tuplets-{Guid.NewGuid():N}.scoreproj");
        try
        {
            ProjectFileService.Save(path, editor.Project);
            Assert.Equal(ProjectFileService.ToJson(editor.Project), ProjectFileService.ToJson(ProjectFileService.Load(path)));
        }
        finally
        {
            File.Delete(path);
        }

        while (editor.CanUndo) editor.Undo();
        Assert.Equal(initial, ProjectFileService.ToJson(editor.Project));
    }

    [Fact]
    public void StartingATuplet_TwiceCancelsIt_AndOnlyKnownNumbersAreOffered()
    {
        var editor = NewEditor();
        Assert.True(editor.StartTuplet(5).Done);
        Assert.True(editor.StartTuplet(5).Done);
        Assert.Null(editor.PendingTuplet);
        Assert.False(editor.StartTuplet(8).Done);
        Assert.False(editor.StartTuplet(1).Done);
        Assert.Equal(new int?[] { 3, 2, 3, 4, 4, 4, null, 8 }, Enumerable.Range(2, 8).Select(ScoreEditor.TupletNormal));
    }
}
