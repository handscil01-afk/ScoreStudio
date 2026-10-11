using System.Windows.Input;
using ScoreStudio.Editing;
using ScoreStudio.Layout;

namespace ScoreStudio.KeyInput;

/// <summary>
/// Turns key presses into editor commands (see docs/SPRINT4_EDITING.md). Durations use Finale's Speedy Entry numbers:
/// 7 whole, 6 half, 5 quarter, 4 eighth, 3 16th, 2 32nd; Ctrl with a number starts a tuplet of that many notes.
/// </summary>
public static class EditorKeys
{
    /// <summary>Runs the command for the key; null when the key is not an editing key (it is then left to the window).</summary>
    public static EditResult? Handle(ScoreEditor editor, Key key, ModifierKeys modifiers)
    {
        var control = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        if ((modifiers & ModifierKeys.Alt) != 0) return null;

        if (control)
        {
            if (TupletCount(key) is { } count) return editor.StartTuplet(count);
            return key switch
            {
                Key.Z when shift => editor.Redo(),
                Key.Z => editor.Undo(),
                Key.Y => editor.Redo(),
                Key.C => editor.CopyMeasure(),
                Key.V => editor.PasteMeasure(),
                Key.Up => editor.Transpose(12),
                Key.Down => editor.Transpose(-12),
                _ => null,
            };
        }

        if (key is >= Key.A and <= Key.G)
        {
            var letter = (char)('A' + (key - Key.A));
            return shift ? editor.AddToChord(letter) : editor.EnterNote(letter);
        }

        if (Duration(key) is { } type) return editor.SetInputValue(type, 0);

        return key switch
        {
            Key.D0 or Key.NumPad0 => editor.EnterRest(),
            Key.OemPeriod or Key.Decimal => editor.ToggleDot(),
            Key.Up => editor.Transpose(1),
            Key.Down => editor.Transpose(-1),
            Key.Left => editor.SelectPrevious(),
            Key.Right => editor.SelectNext(),
            Key.Delete or Key.Back => editor.DeleteSelection(),
            Key.Escape => editor.PendingTuplet is { } pending ? CancelTuplet(editor, pending) : ClearSelection(editor),
            _ => null,
        };
    }

    private static NoteType? Duration(Key key) => key switch
    {
        Key.D7 or Key.NumPad7 => NoteType.Whole,
        Key.D6 or Key.NumPad6 => NoteType.Half,
        Key.D5 or Key.NumPad5 => NoteType.Quarter,
        Key.D4 or Key.NumPad4 => NoteType.Eighth,
        Key.D3 or Key.NumPad3 => NoteType.Sixteenth,
        Key.D2 or Key.NumPad2 => NoteType.ThirtySecond,
        _ => null,
    };

    /// <summary>Ctrl+2 … Ctrl+7 and Ctrl+9: a tuplet of that many notes (Ctrl+3 a triplet). Ctrl+0 stays the window's zoom.</summary>
    private static int? TupletCount(Key key) => key switch
    {
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        Key.D6 or Key.NumPad6 => 6,
        Key.D7 or Key.NumPad7 => 7,
        Key.D9 or Key.NumPad9 => 9,
        _ => null,
    };

    private static EditResult CancelTuplet(ScoreEditor editor, int pending)
    {
        editor.CancelTuplet();
        return EditResult.Ok($"{ScoreEditor.TupletName(pending)} 입력을 취소했습니다.");
    }

    private static EditResult ClearSelection(ScoreEditor editor)
    {
        editor.ClearSelection();
        return EditResult.Ok("선택을 해제했습니다.");
    }
}
