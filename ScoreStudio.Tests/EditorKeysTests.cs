using System.Windows.Input;
using ScoreStudio.Editing;
using ScoreStudio.KeyInput;
using ScoreStudio.Models;

namespace ScoreStudio.Tests;

/// <summary>The window's keys reach the editor as docs/SPRINT4_EDITING.md lists them (no window is shown).</summary>
public sealed class EditorKeysTests
{
    private static int[] Pitches(ScoreEditor editor) =>
        editor.Project.Parts[0].Measures[0].Events.Where(e => e.Kind == "Note").OrderBy(e => e.PositionNumerator * 64 / e.PositionDenominator)
            .ThenBy(e => e.MidiPitch).Select(e => e.MidiPitch).ToArray();

    [Fact]
    public void TypedKeys_EnterAndEditNotes()
    {
        var editor = new ScoreEditor(ScoreProject.CreateNew());
        foreach (var key in new[] { Key.D4, Key.C, Key.D, Key.E, Key.F })
            Assert.True(EditorKeys.Handle(editor, key, ModifierKeys.None)!.Done, key.ToString());
        Assert.Equal(new[] { 72, 74, 76, 77 }, Pitches(editor));
        Assert.All(editor.Project.Parts[0].Measures[0].Events, e => Assert.NotEmpty(e.Beams));

        Assert.True(EditorKeys.Handle(editor, Key.Up, ModifierKeys.None)!.Done);
        Assert.Equal(78, Pitches(editor)[^1]);
        Assert.True(EditorKeys.Handle(editor, Key.Z, ModifierKeys.Control)!.Done);
        Assert.Equal(77, Pitches(editor)[^1]);
        Assert.True(EditorKeys.Handle(editor, Key.Y, ModifierKeys.Control)!.Done);
        Assert.Equal(78, Pitches(editor)[^1]);

        Assert.True(EditorKeys.Handle(editor, Key.A, ModifierKeys.Shift)!.Done);
        Assert.Equal(new[] { 72, 74, 76, 78, 81 }, Pitches(editor));

        Assert.True(EditorKeys.Handle(editor, Key.Left, ModifierKeys.None)!.Done);
        Assert.True(EditorKeys.Handle(editor, Key.Delete, ModifierKeys.None)!.Done);
        Assert.Equal(new[] { 72, 74, 78, 81 }, Pitches(editor));
    }

    [Fact]
    public void CtrlThree_StartsATriplet_AndEscapeCancelsAPendingOne()
    {
        var editor = new ScoreEditor(ScoreProject.CreateNew());
        foreach (var (key, modifiers) in new[] { (Key.D4, ModifierKeys.None), (Key.D3, ModifierKeys.Control), (Key.C, ModifierKeys.None), (Key.D, ModifierKeys.None), (Key.E, ModifierKeys.None) })
            Assert.True(EditorKeys.Handle(editor, key, modifiers)!.Done, key.ToString());
        var tuplet = Assert.Single(editor.Project.Parts[0].Measures[0].Tuplets);
        Assert.Equal((3, 2), (tuplet.Actual, tuplet.Normal));
        Assert.All(editor.Project.Parts[0].Measures[0].Events, e => Assert.Equal((1, 3), (e.DurationNumerator, e.DurationDenominator)));

        Assert.True(EditorKeys.Handle(editor, Key.D5, ModifierKeys.Control)!.Done);
        Assert.Equal(5, editor.PendingTuplet);
        Assert.True(EditorKeys.Handle(editor, Key.Escape, ModifierKeys.None)!.Done);
        Assert.Null(editor.PendingTuplet);
    }

    [Fact]
    public void OtherKeys_AreLeftToTheWindow()
    {
        var editor = new ScoreEditor(ScoreProject.CreateNew());
        Assert.Null(EditorKeys.Handle(editor, Key.H, ModifierKeys.None));
        Assert.Null(EditorKeys.Handle(editor, Key.D0, ModifierKeys.Control)); // zoom reset stays with the window
        Assert.Null(EditorKeys.Handle(editor, Key.C, ModifierKeys.Alt));
        Assert.Empty(editor.Project.Parts[0].Measures[0].Events);
    }
}
