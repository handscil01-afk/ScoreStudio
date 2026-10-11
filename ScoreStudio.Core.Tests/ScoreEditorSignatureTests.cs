using ScoreStudio.Editing;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Core.Tests;

/// <summary>Key, time signature and clef changes (docs/SPRINT4_EDITING.md, signatures).</summary>
public sealed class ScoreEditorSignatureTests
{
    private static Rational R(long n, long d = 1) => new(n, d);

    private static ScoreEditor NewEditor() => new(ScoreProject.CreateNew());

    private static void Enter(ScoreEditor editor, string letters)
    {
        foreach (var letter in letters)
            Assert.True(letter == 'r' ? editor.EnterRest().Done : editor.EnterNote(letter).Done, $"entering {letter}");
    }

    private static List<ScoreMeasure> Measures(ScoreEditor editor, int part = 0) => editor.Project.Parts[part].Measures;

    /// <summary>Start, length and kind of the events of voice 1, staff 1 in a measure, in time order.</summary>
    private static List<(Rational Start, Rational Length, string Kind)> Voice(ScoreEditor editor, int measure) =>
        Measures(editor)[measure].Events.Where(e => e.Staff == 0 && e.Voice == 1)
            .Select(e => (new Rational(e.PositionNumerator, e.PositionDenominator), new Rational(e.DurationNumerator, e.DurationDenominator), e.Kind))
            .OrderBy(e => e.Item1).ToList();

    private static List<GlyphItem> Glyphs(ScoreProject project, char glyph) =>
        ScoreLayoutEngine.Layout(project).Pages.SelectMany(p => p.Items).OfType<GlyphItem>().Where(g => g.Glyph == glyph).ToList();

    [Fact]
    public void KeyChange_RunsToTheNextKeyChange_KeepsPitches_AndKeepsTransposingParts()
    {
        var editor = NewEditor();
        editor.Project.Parts.Add(new ScorePart
        {
            Name = "클라리넷", Measures = Enumerable.Range(1, 4).Select(n => new ScoreMeasure { Number = n, KeyFifths = 2 }).ToList(),
        });
        editor.PlaceCursor(0, 0, 1, R(0));
        Enter(editor, "F");
        var pitch = Measures(editor)[1].Events.Single().MidiPitch;

        editor.PlaceCursor(0, 0, 2, R(0));
        Assert.True(editor.SetKey(-2).Done);
        editor.PlaceCursor(0, 0, 1, R(0));
        var result = editor.SetKey(1);
        Assert.True(result.Done, result.Message);
        Assert.Contains("2마디", result.Message);

        Assert.Equal(new[] { 0, 1, -2, -2 }, Measures(editor).Select(m => m.KeyFifths));
        Assert.Equal(new[] { 2, 3, 0, 0 }, Measures(editor, 1).Select(m => m.KeyFifths)); // a major second up, still
        Assert.Equal(pitch, Measures(editor)[1].Events.Single().MidiPitch);

        // F natural in G major gets a natural sign; the change back to flats cancels the sharp first.
        Assert.NotEmpty(Glyphs(editor.Project, Smufl.AccidentalNatural));
        Assert.Equal(new[] { 0, 1, -2, -2 }, ProjectFileService.FromJson(ProjectFileService.ToJson(editor.Project)).Parts[0].Measures.Select(m => m.KeyFifths));

        // A transposing part that would pass seven sharps refuses the whole change.
        editor.PlaceCursor(0, 0, 0, R(0));
        var before = ProjectFileService.ToJson(editor.Project);
        var refused = editor.SetKey(6);
        Assert.False(refused.Done);
        Assert.Contains("클라리넷", refused.Message);
        Assert.Equal(before, ProjectFileService.ToJson(editor.Project));
        Assert.False(editor.SetKey(0).Done); // already C major

        Assert.True(editor.Undo().Done);
        Assert.True(editor.Undo().Done);
        Assert.Equal(new[] { 0, 0, 0, 0 }, Measures(editor).Select(m => m.KeyFifths));
    }

    [Theory]
    [InlineData(2, 1, 1)]   // D major to G major: the C sharp goes
    [InlineData(2, 0, 2)]   // to C major: every sharp goes
    [InlineData(2, -1, 2)]  // sharps to flats: every sharp goes
    [InlineData(-1, -3, 0)] // more flats: nothing to cancel
    public void KeyChange_InMidScore_CancelsTheDroppedAccidentals(int from, int to, int naturals)
    {
        var editor = NewEditor();
        editor.PlaceCursor(0, 0, 0, R(0));
        if (from != 0) Assert.True(editor.SetKey(from).Done);
        editor.PlaceCursor(0, 0, 2, R(0));
        Assert.True(editor.SetKey(to).Done);

        Assert.Equal(naturals, Glyphs(editor.Project, Smufl.AccidentalNatural).Count);
        // The old key in the first system's header, the new one at the change and in the second system's header (the
        // fourth measure); no naturals there.
        Assert.Equal(new[] { 0, 1, 2, 3 }, ScoreLayoutEngine.Layout(editor.Project).Pages[0].Systems.SelectMany(s => s.Measures).Select(m => m.MeasureIndex));
        Assert.Equal(2, ScoreLayoutEngine.Layout(editor.Project).Pages[0].Systems.Count);
        Assert.Equal(Math.Max(from, 0) + 2 * Math.Max(to, 0), Glyphs(editor.Project, Smufl.AccidentalSharp).Count);
        Assert.Equal(Math.Max(-from, 0) + 2 * Math.Max(-to, 0), Glyphs(editor.Project, Smufl.AccidentalFlat).Count);
    }

    [Fact]
    public void TimeChange_RunsToTheNextTimeChange_AndRefusesToCutNotes()
    {
        var editor = NewEditor();
        editor.PlaceCursor(0, 0, 2, R(0));
        Assert.True(editor.SetTime(2, 4).Done);
        editor.PlaceCursor(0, 0, 0, R(0));
        Enter(editor, "CDEF");

        editor.PlaceCursor(0, 0, 0, R(0));
        var before = ProjectFileService.ToJson(editor.Project);
        var refused = editor.SetTime(3, 4);
        Assert.False(refused.Done);
        Assert.Contains("1마디", refused.Message);
        Assert.Contains("음표", refused.Message);
        Assert.Equal(before, ProjectFileService.ToJson(editor.Project));

        Assert.True(editor.Select(new EditPosition(0, 0, 1, 0, R(3))).Done);
        Assert.True(editor.DeleteSelection().Done);
        Assert.True(editor.SetTime(3, 4).Done);

        Assert.Equal(new[] { (3, 4), (3, 4), (2, 4), (2, 4) }, Measures(editor).Select(m => (m.Numerator, m.Denominator)));
        Assert.Equal(new[] { (R(0), R(1), "Note"), (R(1), R(1), "Note"), (R(2), R(1), "Note") }, Voice(editor, 0));
        Assert.Null(editor.Selection); // the deleted note's rest is gone
        Assert.Equal(R(0), editor.Cursor.Position);

        // Longer again: the full voice gets a rest at the end.
        Assert.True(editor.SetTime(4, 4).Done);
        Assert.Equal((R(3), R(1), "Rest"), Voice(editor, 0)[^1]);
    }

    [Fact]
    public void TimeChange_MakesRestOnlyVoicesWholeMeasureRests_AndRebeamsForANewBeat()
    {
        var editor = NewEditor();
        editor.PlaceCursor(0, 0, 1, R(0));
        Enter(editor, "rr");
        editor.PlaceCursor(0, 0, 0, R(0));
        Assert.True(editor.SetTime(3, 4).Done);
        Assert.Equal(new[] { (R(0), R(3), "Rest") }, Voice(editor, 1));

        editor.SetInputValue(NoteType.Eighth, 0);
        editor.PlaceCursor(0, 0, 0, R(0));
        Enter(editor, "CDEFGA");
        Assert.Equal("Begin,End,Begin,End,Begin,End", string.Join(",", Measures(editor)[0].Events.Select(e => e.Beams.Single())));

        editor.PlaceCursor(0, 0, 0, R(0));
        Assert.True(editor.SetTime(6, 8).Done);
        Assert.Equal("Begin,Continue,End,Begin,Continue,End", string.Join(",", Measures(editor)[0].Events.OrderBy(e => (double)e.PositionNumerator / e.PositionDenominator).Select(e => e.Beams.Single())));
        Assert.Contains(ScoreLayoutEngine.Layout(editor.Project).Pages[0].Systems[0].Measures, m => m.ShowsTimeSignature && m.MeasureIndex == 0);
    }

    [Fact]
    public void ClefChange_AtTheStartReplacesTheClef_ElsewhereAddsAChange_AndDropsRedundantOnes()
    {
        var editor = NewEditor();
        editor.PlaceCursor(0, 0, 0, R(0));
        Assert.True(editor.SetClef(ClefType.Bass).Done);
        Assert.Equal(ClefType.Bass, editor.Project.Parts[0].Staves[0].Clef);
        Assert.Empty(Measures(editor).SelectMany(m => m.ClefChanges));

        Enter(editor, "CDEF");
        var pitches = Measures(editor)[0].Events.Select(e => e.MidiPitch).ToList();
        editor.PlaceCursor(0, 0, 2, R(0));
        Assert.True(editor.SetClef(ClefType.Treble).Done);
        Assert.False(editor.SetClef(ClefType.Treble).Done); // already treble there
        editor.PlaceCursor(0, 0, 3, R(0));
        Assert.False(editor.SetClef(ClefType.Treble).Done); // and still treble after it

        // A change in mid-measure at the selected note; the later change to the same clef is no longer needed.
        Assert.True(editor.Select(new EditPosition(0, 0, 1, 0, R(2))).Done);
        Assert.True(editor.SetClef(ClefType.Treble).Done);
        var change = Measures(editor).SelectMany(m => m.ClefChanges).Single();
        Assert.Equal((2, 1, ClefType.Treble), (change.PositionNumerator, change.PositionDenominator, change.Clef));
        Assert.Equal(pitches, Measures(editor)[0].Events.Select(e => e.MidiPitch));

        // Back to the clef before it: the change goes away instead of repeating that clef.
        Assert.True(editor.SetClef(ClefType.Bass).Done);
        Assert.Empty(Measures(editor).SelectMany(m => m.ClefChanges));

        // The mouse reads pitches in the clef in force.
        editor.PlaceCursor(0, 0, 1, R(0));
        Assert.True(editor.SetClef(ClefType.Alto).Done);
        var layout = ScoreLayoutEngine.Layout(editor.Project);
        var system = layout.Pages[0].Systems[0];
        var measure = system.Measures.Single(m => m.MeasureIndex == 1);
        var target = MouseEntry.Find(layout, 0, editor.Project, NoteType.Quarter, (measure.Left + measure.Right) / 2, system.Staves[0].Top + 2 * layout.StaffSpace);
        Assert.Equal("C4", target?.PitchName); // the alto clef's middle line
        Assert.Single(Glyphs(editor.Project, Smufl.CClef));
    }
}
