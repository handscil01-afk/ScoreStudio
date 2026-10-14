using System.Windows.Input;
using ScoreStudio.Editing;
using ScoreStudio.KeyInput;
using ScoreStudio.Models;

namespace ScoreStudio.Tests;

/// <summary>The window's keys reach the editor through the key map (docs/KEYS_AND_TOOLS.md; no window is shown).</summary>
public sealed class EditorKeysTests
{
    private static int[] Pitches(ScoreEditor editor) =>
        editor.Project.Parts[0].Measures[0].Events.Where(e => e.Kind == "Note").OrderBy(e => e.PositionNumerator * 64 / e.PositionDenominator)
            .ThenBy(e => e.MidiPitch).Select(e => e.MidiPitch).ToArray();

    private static void Press(ScoreEditor editor, KeyMap keys, KeyScope scope, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        var (result, command) = EditorKeys.Handle(editor, keys, scope, key, modifiers);
        Assert.True(result is { Done: true }, $"{modifiers}+{key} ({command}): {result?.Message}");
    }

    [Fact]
    public void SimpleEntryKeys_EnterAndEditNotes()
    {
        var editor = new ScoreEditor(ScoreProject.CreateNew());
        var keys = KeyMap.Defaults();
        foreach (var key in new[] { Key.D4, Key.C, Key.D, Key.E, Key.F })
            Press(editor, keys, KeyScope.Simple, key);
        Assert.Equal(new[] { 72, 74, 76, 77 }, Pitches(editor));
        Assert.All(editor.Project.Parts[0].Measures[0].Events, e => Assert.NotEmpty(e.Beams));

        Press(editor, keys, KeyScope.Simple, Key.Up); // a step: F5 → G5
        Assert.Equal(79, Pitches(editor)[^1]);
        Press(editor, keys, KeyScope.Simple, Key.Z, ModifierKeys.Control);
        Assert.Equal(77, Pitches(editor)[^1]);
        Press(editor, keys, KeyScope.Simple, Key.Add); // a half step
        Assert.Equal(78, Pitches(editor)[^1]);

        Press(editor, keys, KeyScope.Simple, Key.A, ModifierKeys.Shift);
        Assert.Equal(new[] { 72, 74, 76, 78, 81 }, Pitches(editor));
        Press(editor, keys, KeyScope.Simple, Key.Left);
        Press(editor, keys, KeyScope.Simple, Key.Delete);
        Assert.Equal(new[] { 72, 74, 78, 81 }, Pitches(editor));
    }

    [Fact]
    public void SpeedyEntryKeys_EnterAtTheCrossbar_AndCtrlThreeStartsATriplet()
    {
        var editor = new ScoreEditor(ScoreProject.CreateNew());
        var keys = KeyMap.Defaults();
        Press(editor, keys, KeyScope.Speedy, Key.Up);
        Press(editor, keys, KeyScope.Speedy, Key.D3, ModifierKeys.Control);
        foreach (var key in new[] { Key.D4, Key.D4, Key.NumPad4 }) Press(editor, keys, KeyScope.Speedy, key);
        Assert.Equal(new[] { 72, 72, 72 }, Pitches(editor)); // C5, one step above the middle line
        var tuplet = Assert.Single(editor.Project.Parts[0].Measures[0].Tuplets);
        Assert.Equal((3, 2), (tuplet.Actual, tuplet.Normal));

        Press(editor, keys, KeyScope.Speedy, Key.D5, ModifierKeys.Control);
        Assert.Equal(5, editor.PendingTuplet);
        Press(editor, keys, KeyScope.Speedy, Key.Escape);
        Assert.Null(editor.PendingTuplet);
    }

    [Fact]
    public void ChangedKeys_AreUsed_AndUnboundKeysAreLeftToTheWindow()
    {
        var editor = new ScoreEditor(ScoreProject.CreateNew());
        var keys = KeyMap.Defaults();
        keys.Add(KeyScope.Speedy, EditorCommand.EnterQuarter, KeyStroke.Of("Q"));
        Press(editor, keys, KeyScope.Speedy, Key.Q);
        Assert.Single(Pitches(editor));

        Assert.Equal((null, null), EditorKeys.Handle(editor, keys, KeyScope.Speedy, Key.H, ModifierKeys.None));
        Assert.Equal((null, null), EditorKeys.Handle(editor, keys, KeyScope.Simple, Key.D0, ModifierKeys.Control)); // zoom reset stays with the window
        Assert.Equal((null, null), EditorKeys.Handle(editor, keys, KeyScope.Simple, Key.LeftShift, ModifierKeys.Shift));
        var (result, command) = EditorKeys.Handle(editor, keys, KeyScope.Common, Key.L, ModifierKeys.Control);
        Assert.Equal((null, EditorCommand.LyricBox), (result, command)); // the window moves to the lyric box
        Assert.Equal(KeyStroke.Of("Enter"), EditorKeys.Stroke(Key.Return, ModifierKeys.None));
    }
}
