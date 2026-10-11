using ScoreStudio.Editing;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Core.Tests;

/// <summary>Acceptance criteria of docs/SPRINT4_EDITING.md.</summary>
public sealed class ScoreEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScoreStudioEditorTests", Guid.NewGuid().ToString("N"));

    public ScoreEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Rational R(long n, long d = 1) => new(n, d);

    private static ScoreEditor NewEditor() => new(ScoreProject.CreateNew());

    /// <summary>Chords of voice 1, staff 1 of a measure: start, kind and pitches.</summary>
    private static List<(Rational Start, Rational Length, string Kind, int[] Pitches)> Chords(ScoreEditor editor, int measure, int part = 0) =>
        editor.Project.Parts[part].Measures[measure].Events
            .GroupBy(e => new Rational(e.PositionNumerator, e.PositionDenominator)).OrderBy(g => g.Key)
            .Select(g => (g.Key, new Rational(g.First().DurationNumerator, g.First().DurationDenominator), g.First().Kind,
                g.Where(e => e.Kind == "Note").Select(e => e.MidiPitch).OrderBy(p => p).ToArray()))
            .ToList();

    private static void Enter(ScoreEditor editor, string letters)
    {
        foreach (var letter in letters)
            Assert.True(letter == 'r' ? editor.EnterRest().Done : editor.EnterNote(letter).Done, $"entering {letter}");
    }

    [Fact]
    public void EnteringFourQuarters_FillsTheMeasure_NearTheMiddleLine_AndMovesOn()
    {
        var editor = NewEditor();
        Enter(editor, "CDEF");

        Assert.Equal(new[] { (R(0), 72), (R(1), 74), (R(2), 76), (R(3), 77) }, Chords(editor, 0).Select(c => (c.Start, c.Pitches.Single())));
        Assert.Equal(new[] { "C5", "D5", "E5", "F5" }, editor.Project.Parts[0].Measures[0].Events.Select(e => e.NotationSpelling));
        Assert.Equal(new EditPosition(0, 0, 1, 1, R(0)), editor.Cursor);
        Assert.Equal(new EditPosition(0, 0, 1, 0, R(3)), editor.Selection);
    }

    [Fact]
    public void Pitches_FollowTheKey_AndTheClef()
    {
        var editor = NewEditor();
        foreach (var m in editor.Project.Parts[0].Measures) m.KeyFifths = 1;
        Assert.True(editor.EnterNote('F').Done);
        Assert.Equal((66, "F#4"), (editor.Project.Parts[0].Measures[0].Events[0].MidiPitch, editor.Project.Parts[0].Measures[0].Events[0].NotationSpelling));

        var bass = NewEditor();
        bass.Project.Parts[0].Staves[0].Clef = ClefType.Bass;
        Assert.True(bass.EnterNote('C').Done);
        Assert.Equal(48, bass.Project.Parts[0].Measures[0].Events[0].MidiPitch);
    }

    [Fact]
    public void ANoteLongerThanTheRestOfTheMeasure_IsRefused_AndChangesNothing()
    {
        var editor = NewEditor();
        Enter(editor, "CDE");
        Assert.True(editor.SetInputValue(NoteType.Half, 0).Done); // the selection is the entered note: input only
        var before = ProjectFileService.ToJson(editor.Project);
        var cursor = editor.Cursor;

        var result = editor.EnterNote('F');

        Assert.False(result.Done);
        Assert.Contains("남은 길이", result.Message);
        Assert.Equal(before, ProjectFileService.ToJson(editor.Project));
        Assert.Equal(cursor, editor.Cursor);
        Assert.True(editor.Undo().Done); // undoes E, not a refused edit
        Assert.Equal(2, Chords(editor, 0).Count);
    }

    [Fact]
    public void FillingTheLastMeasure_AddsAMeasureToEveryPart()
    {
        var editor = NewEditor();
        editor.Project.Parts.Add(new ScorePart { Name = "파트 2", Measures = Enumerable.Range(1, 4).Select(n => new ScoreMeasure { Number = n }).ToList() });
        Assert.True(editor.PlaceCursor(0, 0, 3, R(0)).Done);
        Enter(editor, "CDEF");

        Assert.All(editor.Project.Parts, p => Assert.Equal(new[] { 1, 2, 3, 4, 5 }, p.Measures.Select(m => m.Number)));
        Assert.Equal(4, editor.Cursor.Measure);
    }

    [Fact]
    public void EnteringOverALongerNote_LeavesTheRestAsARest()
    {
        var editor = NewEditor();
        editor.SetInputValue(NoteType.Half, 0);
        Enter(editor, "C");
        editor.PlaceCursor(0, 0, 0, R(0));
        editor.SetInputValue(NoteType.Quarter, 0);
        Enter(editor, "E");

        Assert.Equal(new[] { (R(0), R(1), "Note"), (R(1), R(1), "Rest") }, Chords(editor, 0).Select(c => (c.Start, c.Length, c.Kind)));
    }

    [Fact]
    public void ChangingTheSelectedLength_LeavesOrTakesRests_ButNeverNotes()
    {
        var editor = NewEditor();
        editor.SetInputValue(NoteType.Half, 0);
        Enter(editor, "C");
        Assert.True(editor.Select(new EditPosition(0, 0, 1, 0, R(0))).Done);

        Assert.True(editor.SetInputValue(NoteType.Quarter, 0).Done);
        Assert.Equal(new[] { (R(0), R(1), "Note"), (R(1), R(1), "Rest") }, Chords(editor, 0).Select(c => (c.Start, c.Length, c.Kind)));

        Assert.True(editor.ToggleDot().Done); // a dotted quarter takes half of the rest after it
        Assert.Equal(new[] { (R(0), R(3, 2), "Note"), (R(3, 2), R(1, 2), "Rest") }, Chords(editor, 0).Select(c => (c.Start, c.Length, c.Kind)));

        editor.PlaceCursor(0, 0, 0, R(2));
        Enter(editor, "G");
        editor.Select(new EditPosition(0, 0, 1, 0, R(0)));
        var refused = editor.SetInputValue(NoteType.Whole, 0);
        Assert.False(refused.Done);
        Assert.Contains("뒤에 음표", refused.Message);
        Assert.Equal(R(3, 2), Chords(editor, 0)[0].Length);
    }

    [Fact]
    public void DeleteTransposeAndChords_FollowTheRules()
    {
        var editor = NewEditor();
        Enter(editor, "C");
        var c = new EditPosition(0, 0, 1, 0, R(0));
        editor.Select(c);

        Assert.True(editor.Transpose(1).Done);
        Assert.Equal(73, Chords(editor, 0)[0].Pitches.Single());
        Assert.True(editor.Transpose(-13).Done);
        Assert.Equal(60, Chords(editor, 0)[0].Pitches.Single());

        Assert.True(editor.AddToChord('E').Done);
        Assert.True(editor.AddToChord('C').Done);
        Assert.Equal(new[] { 60, 64, 72 }, Chords(editor, 0)[0].Pitches);

        Assert.True(editor.DeleteSelection().Done);
        Assert.Equal(("Rest", R(1)), (Chords(editor, 0)[0].Kind, Chords(editor, 0)[0].Length));
        Assert.False(editor.DeleteSelection().Done);

        Assert.True(editor.AddToChord('G').Done); // a selected rest becomes the note
        Assert.Equal(("Note", 67), (Chords(editor, 0)[0].Kind, Chords(editor, 0)[0].Pitches.Single()));
    }

    [Fact]
    public void UndoingEverything_RestoresTheStart_AndRedoRestoresTheEnd()
    {
        var editor = NewEditor();
        var start = ProjectFileService.ToJson(editor.Project);

        Enter(editor, "CDEFG");
        editor.Select(new EditPosition(0, 0, 1, 0, R(1)));
        editor.Transpose(2);
        editor.SetInputValue(NoteType.Eighth, 0);
        editor.DeleteSelection();
        editor.InsertMeasureAfter();
        editor.AddToChord('A');
        var end = ProjectFileService.ToJson(editor.Project);

        var undone = 0;
        while (editor.CanUndo)
        {
            Assert.True(editor.Undo().Done);
            undone++;
        }

        Assert.True(undone >= 9);
        Assert.Equal(start, ProjectFileService.ToJson(editor.Project));
        Assert.Equal(new EditPosition(0, 0, 1, 0, R(0)), editor.Cursor);

        while (editor.CanRedo) Assert.True(editor.Redo().Done);
        Assert.Equal(end, ProjectFileService.ToJson(editor.Project));

        editor.Undo();
        Assert.True(editor.CanRedo);
        Assert.True(editor.InsertMeasureAfter().Done);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void Measures_CanBeAddedDeletedCopiedAndPasted()
    {
        var editor = NewEditor();
        editor.PlaceCursor(0, 0, 1, R(0));
        Assert.True(editor.InsertMeasureAfter().Done);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, editor.Project.Parts[0].Measures.Select(m => m.Number));

        editor.Project.Parts[0].Measures[3].SystemBreak = true;
        editor.PlaceCursor(0, 0, 3, R(0));
        Assert.True(editor.DeleteMeasure().Done);
        Assert.Equal(new[] { 1, 2, 3, 4 }, editor.Project.Parts[0].Measures.Select(m => m.Number));
        Assert.True(editor.Project.Parts[0].Measures[3].SystemBreak);

        editor.PlaceCursor(0, 0, 0, R(0));
        Enter(editor, "CDEF");
        editor.PlaceCursor(0, 0, 0, R(0));
        Assert.True(editor.CopyMeasure().Done);
        editor.PlaceCursor(0, 0, 2, R(0));
        Assert.True(editor.PasteMeasure().Done);
        Assert.Equal(Chords(editor, 0).Select(c => (c.Start, c.Pitches[0])), Chords(editor, 2).Select(c => (c.Start, c.Pitches[0])));

        editor.Project.Parts[0].Measures[3].Numerator = 3;
        editor.PlaceCursor(0, 0, 3, R(0));
        Assert.False(editor.PasteMeasure().Done);

        var single = new ScoreEditor(new ScoreProject { Parts = { new ScorePart { Measures = { new ScoreMeasure() } } } });
        Assert.False(single.DeleteMeasure().Done);
    }

    [Fact]
    public void EnteredNotes_AreBeamedByBeat()
    {
        var editor = NewEditor();
        editor.SetInputValue(NoteType.Eighth, 0);
        Enter(editor, "CDEF");
        Assert.Equal(new[] { "Begin", "End", "Begin", "End" },
            editor.Project.Parts[0].Measures[0].Events.Select(e => string.Join(",", e.Beams)));

        Enter(editor, "CrD");
        var m1 = editor.Project.Parts[0].Measures[1].Events;
        Assert.All(m1, e => Assert.Empty(e.Beams));

        editor.PlaceCursor(0, 0, 2, R(0));
        editor.SetInputValue(NoteType.Eighth, 1);
        Enter(editor, "C");
        editor.SetInputValue(NoteType.Sixteenth, 0);
        Enter(editor, "D");
        Assert.Equal(new[] { "Begin", "End,BackwardHook" }, editor.Project.Parts[0].Measures[2].Events.Select(e => string.Join(",", e.Beams)));

        var compound = NewEditor();
        foreach (var m in compound.Project.Parts[0].Measures) (m.Numerator, m.Denominator) = (6, 8);
        compound.SetInputValue(NoteType.Eighth, 0);
        Enter(compound, "CDEFGA");
        Assert.Equal(new[] { "Begin", "Continue", "End", "Begin", "Continue", "End" },
            compound.Project.Parts[0].Measures[0].Events.Select(e => string.Join(",", e.Beams)));
    }

    [Fact]
    public void AnEditedScore_SavesAndLaysOut()
    {
        var editor = NewEditor();
        editor.SetInputValue(NoteType.Eighth, 0);
        Enter(editor, "CDEFGABC");
        editor.SetInputValue(NoteType.Quarter, 1);
        Enter(editor, "Dr");
        editor.SetInputValue(NoteType.Eighth, 0);
        Enter(editor, "E");
        editor.Select(new EditPosition(0, 0, 1, 1, R(0)));
        editor.AddToChord('F');

        var path = Path.Combine(_dir, "edited.scoreproj");
        ProjectFileService.Save(path, editor.Project);
        Assert.Equal(ProjectFileService.ToJson(editor.Project), ProjectFileService.ToJson(ProjectFileService.Load(path)));

        var page = ScoreLayoutEngine.Layout(editor.Project).Pages.Single();
        Assert.Equal(4, page.Items.OfType<BeamItem>().Count());
    }

    [Fact]
    public void ChordBoxes_FindTheDrawnNotes()
    {
        var editor = NewEditor();
        Enter(editor, "CDEFrG");
        editor.Select(new EditPosition(0, 0, 1, 1, R(1)));
        editor.AddToChord('B');
        var layout = ScoreLayoutEngine.Layout(editor.Project);
        var page = layout.Pages.Single();
        var sp = layout.StaffSpace;

        Assert.Equal(6, page.Chords.Count);
        foreach (var head in page.Items.OfType<GlyphItem>().Where(g => g.Glyph == Smufl.NoteheadBlack))
        {
            var hit = LayoutHitTest.FindChord(page, head.X + 0.5 * sp, head.Y, 0.2 * sp);
            Assert.NotNull(hit);
            Assert.False(hit.IsRest);
        }

        var rest = page.Chords.Single(c => c.IsRest);
        Assert.Equal((1, R(0)), (rest.MeasureIndex, rest.Start));
        Assert.Equal(rest, LayoutHitTest.FindChord(page, (rest.Left + rest.Right) / 2, (rest.Top + rest.Bottom) / 2, 0));

        var chord = page.Chords.Single(c => c.MeasureIndex == 1 && c.Start == R(1));
        Assert.Equal(2 * sp, chord.Bottom - chord.Top, 6); // G5 and B5, a space apart, in one box
        Assert.True(editor.Select(new EditPosition(chord.PartIndex, chord.Staff, chord.Voice, chord.MeasureIndex, chord.Start)).Done);

        var system = page.Systems[0];
        var measure = system.Measures[2];
        Assert.Equal(new StaffMeasureHit(0, 0, 2),
            LayoutHitTest.FindStaffMeasure(page, (measure.Left + measure.Right) / 2, system.Staves[0].Top + 2 * sp, sp));
        Assert.Null(LayoutHitTest.FindStaffMeasure(page, 1, 1, sp));
    }

    [Fact]
    public void EditMarks_BoxTheSelection_AndPutTheCursorBeforeItsNote()
    {
        var editor = NewEditor();
        Enter(editor, "CDE");
        var layout = ScoreLayoutEngine.Layout(editor.Project);
        var page = layout.Pages[0];

        // After entering: E is selected, the cursor waits after it in the same measure.
        var marks = EditMarks.Find(layout, editor.Selection, editor.Cursor);
        var e = page.Chords.Single(c => c.Start == R(2));
        Assert.Equal((0, e), (marks.SelectionPage, marks.Selection));
        Assert.Equal(0, marks.CursorPage);
        Assert.True(marks.CursorX > e.Right && marks.CursorX < page.Systems[0].Measures[0].Right);

        // On a selected note the cursor stands just before it, spanning the staff.
        editor.Select(new EditPosition(0, 0, 1, 0, R(1)));
        marks = EditMarks.Find(layout, editor.Selection, editor.Cursor);
        var d = page.Chords.Single(c => c.Start == R(1));
        Assert.True(marks.CursorX < d.Left && marks.CursorX > page.Chords.Single(c => c.Start == R(0)).Right);
        var staffTop = page.Systems[0].Staves[0].Top;
        Assert.True(marks.CursorTop < staffTop && marks.CursorBottom > staffTop + 4 * layout.StaffSpace);

        // An empty measure: the cursor sits at its start.
        editor.PlaceCursor(0, 0, 2, R(0));
        marks = EditMarks.Find(layout, editor.Selection, editor.Cursor);
        Assert.Null(marks.Selection);
        var empty = page.Systems[0].Measures[2];
        Assert.True(marks.CursorX > empty.Left && marks.CursorX < empty.Left + 2 * layout.StaffSpace);
    }
}
