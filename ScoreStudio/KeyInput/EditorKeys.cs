using System.Windows.Input;
using ScoreStudio.Editing;

namespace ScoreStudio.KeyInput;

/// <summary>
/// Turns key presses into editor commands through the user's key map (docs/KEYS_AND_TOOLS.md): the current tool's keys
/// first (Speedy or Simple Entry), then the common ones.
/// </summary>
public static class EditorKeys
{
    /// <summary>
    /// Runs the command bound to the key. Returns the editor's result, or only the command when the window must do it
    /// (text boxes, mouse entry); both null when the key is not bound (it is then left to the window).
    /// </summary>
    public static (EditResult? Result, EditorCommand? Command) Handle(ScoreEditor editor, KeyMap keys, KeyScope scope, Key key, ModifierKeys modifiers)
    {
        if (Stroke(key, modifiers) is not { } stroke || keys.Find(scope, stroke) is not { } command) return (null, null);
        return (CommandRunner.Run(editor, command), command);
    }

    /// <summary>The key as the key map names it; null for keys that are only modifiers or input-method states.</summary>
    public static KeyStroke? Stroke(Key key, ModifierKeys modifiers)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.HangulMode or Key.HanjaMode)
            return null;
        return KeyStroke.Of(key.ToString(), (modifiers & ModifierKeys.Control) != 0, (modifiers & ModifierKeys.Shift) != 0, (modifiers & ModifierKeys.Alt) != 0);
    }
}
