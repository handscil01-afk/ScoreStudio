using System.Text;
using ScoreStudio.Layout;

namespace ScoreStudio.Editing;

/// <summary>Editing commands a key can run (docs/KEYS_AND_TOOLS.md). Stored in settings by name.</summary>
public enum EditorCommand
{
    Undo, Redo, CopyMeasure, PasteMeasure, SelectPrevious, SelectNext, Delete, Cancel, OctaveUp, OctaveDown,
    Tuplet2, Tuplet3, Tuplet4, Tuplet5, Tuplet6, Tuplet7, Tuplet9, LyricBox, ChordBox,

    // Speedy Entry: a value key enters a note at the crossbar (or changes the chosen note's value).
    EnterSixtyFourth, EnterThirtySecond, EnterSixteenth, EnterEighth, EnterQuarter, EnterHalf, EnterWhole, EnterBreve,
    Dot, AddDot, AddAtCrossbar, ToRest, SemitoneUp, SemitoneDown, StepUp, StepDown, CrossbarUp, CrossbarDown, StaffUp, StaffDown,
    TieToNext, Respell, FlipStem, AutoStem,

    // Simple Entry: a value key chooses the length, a letter enters the note.
    NoteC, NoteD, NoteE, NoteF, NoteG, NoteA, NoteB, AddC, AddD, AddE, AddF, AddG, AddA, AddB,
    ChooseWhole, ChooseHalf, ChooseQuarter, ChooseEighth, ChooseSixteenth, ChooseThirtySecond, EnterRest, MouseEntry,

    // Finale has these; ScoreStudio does not yet.
    BreakBeam, GraceNote, HideEntry,
}

/// <summary>Where a key works: everywhere, or only with the Speedy or Simple Entry tool.</summary>
public enum KeyScope
{
    Common,
    Speedy,
    Simple,
}

/// <summary>A command's name and group in Korean, and whether ScoreStudio can do it yet.</summary>
public sealed record CommandInfo(EditorCommand Command, string Group, string Name, bool Supported = true);

/// <summary>
/// A key with its modifiers, named as WPF names keys ("D3", "NumPad3", "OemPlus", "Up", "Enter"), so the Core can store and
/// test key settings without WPF. Written as "Ctrl+Shift+Z".
/// </summary>
public sealed record KeyStroke(string Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    /// <summary>Other names WPF has for the same key, and the one used here.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = "Enter", ["Prior"] = "PageUp", ["Next"] = "PageDown", ["Capital"] = "CapsLock", ["Backspace"] = "Back",
        ["Esc"] = "Escape", ["Del"] = "Delete", ["Oem2"] = "OemQuestion", ["Oem1"] = "OemSemicolon", ["Oem3"] = "OemTilde",
    };

    public static string Normalize(string key) => Aliases.TryGetValue(key, out var name) ? name : key;

    public static KeyStroke Of(string key, bool ctrl = false, bool shift = false, bool alt = false) => new(Normalize(key), ctrl, shift, alt);

    public override string ToString() => $"{(Ctrl ? "Ctrl+" : "")}{(Shift ? "Shift+" : "")}{(Alt ? "Alt+" : "")}{Key}";

    /// <summary>Reads "Ctrl+Shift+Z"; null when it is not a key.</summary>
    public static KeyStroke? Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[^1].Length == 0) return null;
        bool ctrl = false, shift = false, alt = false;
        foreach (var modifier in parts[..^1])
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default: return null;
            }
        }

        var key = Normalize(parts[^1]);
        return key.All(c => char.IsAsciiLetterOrDigit(c)) ? new KeyStroke(key, ctrl, shift, alt) : null;
    }

    /// <summary>How the key is shown to people: "Ctrl+Shift+Z", "1", "숫자판 +", "↑".</summary>
    public string Display()
    {
        var name = Key switch
        {
            _ when Key.Length == 2 && Key[0] == 'D' && char.IsAsciiDigit(Key[1]) => Key[1..],
            _ when Key.StartsWith("NumPad", StringComparison.Ordinal) => "숫자판 " + Key[6..],
            "Add" => "숫자판 +", "Subtract" => "숫자판 -", "Multiply" => "숫자판 *", "Divide" => "숫자판 /", "Decimal" => "숫자판 .",
            "OemPlus" => "=", "OemMinus" => "-", "OemPeriod" => ".", "OemComma" => ",", "OemQuestion" => "/", "OemSemicolon" => ";",
            "Up" => "↑", "Down" => "↓", "Left" => "←", "Right" => "→", "Back" => "Backspace", "Escape" => "Esc",
            _ => Key,
        };
        var text = new StringBuilder();
        if (Ctrl) text.Append("Ctrl+");
        if (Shift) text.Append("Shift+");
        if (Alt) text.Append("Alt+");
        return text.Append(name).ToString();
    }
}

/// <summary>
/// Which key runs which command in each scope: the defaults (Finale's Speedy and Simple Entry keys) with the user's
/// changes on top. A tool's scope is looked in before the common one, so a tool may give a common key another use.
/// </summary>
public sealed class KeyMap
{
    private readonly Dictionary<(KeyScope Scope, EditorCommand Command), List<KeyStroke>> _keys = new();

    public static readonly IReadOnlyList<CommandInfo> Commands = new CommandInfo[]
    {
        new(EditorCommand.Undo, "편집", "실행 취소"),
        new(EditorCommand.Redo, "편집", "다시 실행"),
        new(EditorCommand.CopyMeasure, "편집", "마디 복사"),
        new(EditorCommand.PasteMeasure, "편집", "마디 붙여넣기"),
        new(EditorCommand.SelectPrevious, "이동", "이전 음표"),
        new(EditorCommand.SelectNext, "이동", "다음 음표"),
        new(EditorCommand.Delete, "편집", "음표 지우기(쉼표로)"),
        new(EditorCommand.Cancel, "편집", "선택 해제·잇단음표 취소"),
        new(EditorCommand.OctaveUp, "음높이", "옥타브 올림"),
        new(EditorCommand.OctaveDown, "음높이", "옥타브 내림"),
        new(EditorCommand.Tuplet2, "잇단음표", "둘잇단음표"),
        new(EditorCommand.Tuplet3, "잇단음표", "셋잇단음표"),
        new(EditorCommand.Tuplet4, "잇단음표", "넷잇단음표"),
        new(EditorCommand.Tuplet5, "잇단음표", "다섯잇단음표"),
        new(EditorCommand.Tuplet6, "잇단음표", "여섯잇단음표"),
        new(EditorCommand.Tuplet7, "잇단음표", "일곱잇단음표"),
        new(EditorCommand.Tuplet9, "잇단음표", "아홉잇단음표"),
        new(EditorCommand.LyricBox, "글자", "가사 칸으로"),
        new(EditorCommand.ChordBox, "글자", "코드 이름 칸으로"),
        new(EditorCommand.EnterSixtyFourth, "길이", "64분음표"),
        new(EditorCommand.EnterThirtySecond, "길이", "32분음표"),
        new(EditorCommand.EnterSixteenth, "길이", "16분음표"),
        new(EditorCommand.EnterEighth, "길이", "8분음표"),
        new(EditorCommand.EnterQuarter, "길이", "4분음표"),
        new(EditorCommand.EnterHalf, "길이", "2분음표"),
        new(EditorCommand.EnterWhole, "길이", "온음표"),
        new(EditorCommand.EnterBreve, "길이", "겹온음표"),
        new(EditorCommand.Dot, "길이", "입력 길이에 점 붙이기·떼기"),
        new(EditorCommand.AddDot, "길이", "고른 음표에 점 붙이기·떼기"),
        new(EditorCommand.AddAtCrossbar, "음높이", "입력 높이의 음을 화음에 더하기·빼기"),
        new(EditorCommand.ToRest, "편집", "음을 쉼표로"),
        new(EditorCommand.SemitoneUp, "음높이", "반음 올림"),
        new(EditorCommand.SemitoneDown, "음높이", "반음 내림"),
        new(EditorCommand.StepUp, "음높이", "한 칸 위로"),
        new(EditorCommand.StepDown, "음높이", "한 칸 아래로"),
        new(EditorCommand.CrossbarUp, "음높이", "입력 높이만 위로"),
        new(EditorCommand.CrossbarDown, "음높이", "입력 높이만 아래로"),
        new(EditorCommand.StaffUp, "이동", "위 보표로"),
        new(EditorCommand.StaffDown, "이동", "아래 보표로"),
        new(EditorCommand.TieToNext, "음표", "다음 음과 붙임줄"),
        new(EditorCommand.Respell, "음높이", "딴이름한소리(엔하모닉)"),
        new(EditorCommand.FlipStem, "음표", "기둥 방향 뒤집기"),
        new(EditorCommand.AutoStem, "음표", "기둥 방향 자동"),
        new(EditorCommand.NoteC, "음 이름", "C 넣기"),
        new(EditorCommand.NoteD, "음 이름", "D 넣기"),
        new(EditorCommand.NoteE, "음 이름", "E 넣기"),
        new(EditorCommand.NoteF, "음 이름", "F 넣기"),
        new(EditorCommand.NoteG, "음 이름", "G 넣기"),
        new(EditorCommand.NoteA, "음 이름", "A 넣기"),
        new(EditorCommand.NoteB, "음 이름", "B 넣기"),
        new(EditorCommand.AddC, "음 이름", "화음에 C 더하기"),
        new(EditorCommand.AddD, "음 이름", "화음에 D 더하기"),
        new(EditorCommand.AddE, "음 이름", "화음에 E 더하기"),
        new(EditorCommand.AddF, "음 이름", "화음에 F 더하기"),
        new(EditorCommand.AddG, "음 이름", "화음에 G 더하기"),
        new(EditorCommand.AddA, "음 이름", "화음에 A 더하기"),
        new(EditorCommand.AddB, "음 이름", "화음에 B 더하기"),
        new(EditorCommand.ChooseWhole, "길이", "온음표 고르기"),
        new(EditorCommand.ChooseHalf, "길이", "2분음표 고르기"),
        new(EditorCommand.ChooseQuarter, "길이", "4분음표 고르기"),
        new(EditorCommand.ChooseEighth, "길이", "8분음표 고르기"),
        new(EditorCommand.ChooseSixteenth, "길이", "16분음표 고르기"),
        new(EditorCommand.ChooseThirtySecond, "길이", "32분음표 고르기"),
        new(EditorCommand.EnterRest, "길이", "쉼표 넣기"),
        new(EditorCommand.MouseEntry, "도구", "마우스 입력 켜기·끄기"),
        new(EditorCommand.BreakBeam, "음표", "빔 끊기·잇기", Supported: false),
        new(EditorCommand.GraceNote, "음표", "꾸밈음", Supported: false),
        new(EditorCommand.HideEntry, "음표", "음표 숨기기", Supported: false),
    };

    public static CommandInfo Info(EditorCommand command) => Commands.First(c => c.Command == command);

    /// <summary>Finale's keys (docs/KEYS_AND_TOOLS.md, defaults).</summary>
    public static KeyMap Defaults()
    {
        var map = new KeyMap();
        void Bind(KeyScope scope, EditorCommand command, params KeyStroke[] keys) => map._keys[(scope, command)] = keys.ToList();
        static KeyStroke K(string key, bool ctrl = false, bool shift = false, bool alt = false) => KeyStroke.Of(key, ctrl, shift, alt);

        Bind(KeyScope.Common, EditorCommand.Undo, K("Z", ctrl: true));
        Bind(KeyScope.Common, EditorCommand.Redo, K("Y", ctrl: true), K("Z", ctrl: true, shift: true));
        Bind(KeyScope.Common, EditorCommand.CopyMeasure, K("C", ctrl: true));
        Bind(KeyScope.Common, EditorCommand.PasteMeasure, K("V", ctrl: true));
        Bind(KeyScope.Common, EditorCommand.SelectPrevious, K("Left"));
        Bind(KeyScope.Common, EditorCommand.SelectNext, K("Right"));
        Bind(KeyScope.Common, EditorCommand.Delete, K("Delete"));
        Bind(KeyScope.Common, EditorCommand.Cancel, K("Escape"));
        Bind(KeyScope.Common, EditorCommand.OctaveUp, K("Up", ctrl: true));
        Bind(KeyScope.Common, EditorCommand.OctaveDown, K("Down", ctrl: true));
        foreach (var (command, digit) in new[] { (EditorCommand.Tuplet2, 2), (EditorCommand.Tuplet3, 3), (EditorCommand.Tuplet4, 4), (EditorCommand.Tuplet5, 5), (EditorCommand.Tuplet6, 6), (EditorCommand.Tuplet7, 7), (EditorCommand.Tuplet9, 9) })
            Bind(KeyScope.Common, command, K($"D{digit}", ctrl: true), K($"NumPad{digit}", ctrl: true));
        Bind(KeyScope.Common, EditorCommand.LyricBox, K("L", ctrl: true));
        Bind(KeyScope.Common, EditorCommand.ChordBox, K("K", ctrl: true));

        var values = new[]
        {
            EditorCommand.EnterSixtyFourth, EditorCommand.EnterThirtySecond, EditorCommand.EnterSixteenth, EditorCommand.EnterEighth,
            EditorCommand.EnterQuarter, EditorCommand.EnterHalf, EditorCommand.EnterWhole, EditorCommand.EnterBreve,
        };
        for (var i = 0; i < values.Length; i++) Bind(KeyScope.Speedy, values[i], K($"D{i + 1}"), K($"NumPad{i + 1}"));
        Bind(KeyScope.Speedy, EditorCommand.AddDot, K("OemPeriod"), K("Decimal"));
        Bind(KeyScope.Speedy, EditorCommand.AddAtCrossbar, K("Enter"));
        Bind(KeyScope.Speedy, EditorCommand.ToRest, K("Back"));
        Bind(KeyScope.Speedy, EditorCommand.SemitoneUp, K("OemPlus", shift: true), K("Add"));
        Bind(KeyScope.Speedy, EditorCommand.SemitoneDown, K("OemMinus"), K("Subtract"));
        Bind(KeyScope.Speedy, EditorCommand.StepUp, K("Up"));
        Bind(KeyScope.Speedy, EditorCommand.StepDown, K("Down"));
        Bind(KeyScope.Speedy, EditorCommand.CrossbarUp, K("Up", alt: true));
        Bind(KeyScope.Speedy, EditorCommand.CrossbarDown, K("Down", alt: true));
        Bind(KeyScope.Speedy, EditorCommand.StaffUp, K("Up", shift: true));
        Bind(KeyScope.Speedy, EditorCommand.StaffDown, K("Down", shift: true));
        Bind(KeyScope.Speedy, EditorCommand.TieToNext, K("T"));
        Bind(KeyScope.Speedy, EditorCommand.Respell, K("D9"), K("NumPad9"));
        Bind(KeyScope.Speedy, EditorCommand.FlipStem, K("L"));
        Bind(KeyScope.Speedy, EditorCommand.AutoStem, K("L", ctrl: true));

        foreach (var (note, add, letter) in new[]
                 {
                     (EditorCommand.NoteC, EditorCommand.AddC, "C"), (EditorCommand.NoteD, EditorCommand.AddD, "D"), (EditorCommand.NoteE, EditorCommand.AddE, "E"),
                     (EditorCommand.NoteF, EditorCommand.AddF, "F"), (EditorCommand.NoteG, EditorCommand.AddG, "G"), (EditorCommand.NoteA, EditorCommand.AddA, "A"),
                     (EditorCommand.NoteB, EditorCommand.AddB, "B"),
                 })
        {
            Bind(KeyScope.Simple, note, K(letter));
            Bind(KeyScope.Simple, add, K(letter, shift: true));
        }

        var choices = new[] { (EditorCommand.ChooseWhole, 7), (EditorCommand.ChooseHalf, 6), (EditorCommand.ChooseQuarter, 5), (EditorCommand.ChooseEighth, 4), (EditorCommand.ChooseSixteenth, 3), (EditorCommand.ChooseThirtySecond, 2) };
        foreach (var (command, digit) in choices) Bind(KeyScope.Simple, command, K($"D{digit}"), K($"NumPad{digit}"));
        Bind(KeyScope.Simple, EditorCommand.EnterRest, K("D0"), K("NumPad0"));
        Bind(KeyScope.Simple, EditorCommand.Dot, K("OemPeriod"), K("Decimal"));
        Bind(KeyScope.Simple, EditorCommand.StepUp, K("Up"));
        Bind(KeyScope.Simple, EditorCommand.StepDown, K("Down"));
        Bind(KeyScope.Simple, EditorCommand.SemitoneUp, K("OemPlus", shift: true), K("Add"));
        Bind(KeyScope.Simple, EditorCommand.SemitoneDown, K("OemMinus"), K("Subtract"));
        Bind(KeyScope.Simple, EditorCommand.ToRest, K("Back"));
        Bind(KeyScope.Simple, EditorCommand.MouseEntry, K("N"));
        return map;
    }

    /// <summary>The keys of a command in a scope.</summary>
    public IReadOnlyList<KeyStroke> KeysFor(KeyScope scope, EditorCommand command) =>
        _keys.TryGetValue((scope, command), out var keys) ? keys : Array.Empty<KeyStroke>();

    /// <summary>Every scope and command that has keys or could have them, for the settings window.</summary>
    public IEnumerable<(KeyScope Scope, EditorCommand Command, IReadOnlyList<KeyStroke> Keys)> All() =>
        _keys.OrderBy(k => k.Key.Scope).ThenBy(k => k.Key.Command).Select(k => (k.Key.Scope, k.Key.Command, (IReadOnlyList<KeyStroke>)k.Value));

    /// <summary>The command a key runs with a tool: the tool's scope first, then the common keys.</summary>
    public EditorCommand? Find(KeyScope tool, KeyStroke key)
    {
        foreach (var scope in tool == KeyScope.Common ? new[] { KeyScope.Common } : new[] { tool, KeyScope.Common })
        {
            foreach (var ((s, command), keys) in _keys)
            {
                if (s == scope && keys.Contains(key)) return command;
            }
        }

        return null;
    }

    /// <summary>Gives a command a key in a scope; another command of the scope that had it loses it. Returns that command.</summary>
    public EditorCommand? Add(KeyScope scope, EditorCommand command, KeyStroke key)
    {
        EditorCommand? previous = null;
        foreach (var ((s, other), keys) in _keys)
        {
            if (s == scope && other != command && keys.Remove(key)) previous = other;
        }

        if (!_keys.TryGetValue((scope, command), out var list)) _keys[(scope, command)] = list = new List<KeyStroke>();
        if (!list.Contains(key)) list.Add(key);
        return previous;
    }

    public void Remove(KeyScope scope, EditorCommand command, KeyStroke key)
    {
        if (_keys.TryGetValue((scope, command), out var list)) list.Remove(key);
    }

    /// <summary>Puts back a command's default keys (taking them from commands that have them now).</summary>
    public void Reset(KeyScope scope, EditorCommand command)
    {
        _keys[(scope, command)] = new List<KeyStroke>();
        foreach (var key in Defaults().KeysFor(scope, command)) Add(scope, command, key);
    }

    /// <summary>The user's changes for the settings file: the commands whose keys differ from the defaults ("Speedy:StepUp" → ["Up"]).</summary>
    public Dictionary<string, List<string>> Changes()
    {
        var defaults = Defaults();
        var changes = new Dictionary<string, List<string>>();
        foreach (var (scope, command) in _keys.Keys.Union(defaults._keys.Keys))
        {
            var keys = KeysFor(scope, command);
            if (!keys.ToHashSet().SetEquals(defaults.KeysFor(scope, command))) changes[$"{scope}:{command}"] = keys.Select(k => k.ToString()).ToList();
        }

        return changes;
    }

    /// <summary>The defaults with the saved changes applied; unknown commands or keys in the file are left out.</summary>
    public static KeyMap WithChanges(IReadOnlyDictionary<string, List<string>>? changes)
    {
        var map = Defaults();
        if (changes is null) return map;
        foreach (var (name, keys) in changes)
        {
            var parts = name.Split(':');
            if (parts.Length != 2 || !Enum.TryParse<KeyScope>(parts[0], out var scope) || !Enum.TryParse<EditorCommand>(parts[1], out var command)
                || !Enum.IsDefined(scope) || !Enum.IsDefined(command))
                continue;
            map._keys[(scope, command)] = new List<KeyStroke>();
            foreach (var key in (keys ?? new List<string>()).Select(k => k is null ? null : KeyStroke.Parse(k)).OfType<KeyStroke>())
                map.Add(scope, command, key);
        }

        return map;
    }
}

/// <summary>Runs a command on the editor; null for the commands the window does itself (text boxes, mouse entry).</summary>
public static class CommandRunner
{
    public static EditResult? Run(ScoreEditor editor, EditorCommand command) => command switch
    {
        EditorCommand.Undo => editor.Undo(),
        EditorCommand.Redo => editor.Redo(),
        EditorCommand.CopyMeasure => editor.CopyMeasure(),
        EditorCommand.PasteMeasure => editor.PasteMeasure(),
        EditorCommand.SelectPrevious => editor.SelectPrevious(),
        EditorCommand.SelectNext => editor.SelectNext(),
        EditorCommand.Delete or EditorCommand.ToRest => editor.DeleteSelection(),
        EditorCommand.Cancel => Cancel(editor),
        EditorCommand.OctaveUp => editor.Transpose(12),
        EditorCommand.OctaveDown => editor.Transpose(-12),
        EditorCommand.Tuplet2 => editor.StartTuplet(2),
        EditorCommand.Tuplet3 => editor.StartTuplet(3),
        EditorCommand.Tuplet4 => editor.StartTuplet(4),
        EditorCommand.Tuplet5 => editor.StartTuplet(5),
        EditorCommand.Tuplet6 => editor.StartTuplet(6),
        EditorCommand.Tuplet7 => editor.StartTuplet(7),
        EditorCommand.Tuplet9 => editor.StartTuplet(9),
        EditorCommand.EnterSixtyFourth => editor.EnterAtCrossbar(NoteType.SixtyFourth),
        EditorCommand.EnterThirtySecond => editor.EnterAtCrossbar(NoteType.ThirtySecond),
        EditorCommand.EnterSixteenth => editor.EnterAtCrossbar(NoteType.Sixteenth),
        EditorCommand.EnterEighth => editor.EnterAtCrossbar(NoteType.Eighth),
        EditorCommand.EnterQuarter => editor.EnterAtCrossbar(NoteType.Quarter),
        EditorCommand.EnterHalf => editor.EnterAtCrossbar(NoteType.Half),
        EditorCommand.EnterWhole => editor.EnterAtCrossbar(NoteType.Whole),
        EditorCommand.EnterBreve => editor.EnterAtCrossbar(NoteType.Breve),
        EditorCommand.Dot => editor.ToggleDot(),
        EditorCommand.AddDot => editor.AddDot(),
        EditorCommand.AddAtCrossbar => editor.AddAtCrossbar(),
        EditorCommand.SemitoneUp => editor.Transpose(1),
        EditorCommand.SemitoneDown => editor.Transpose(-1),
        EditorCommand.StepUp => editor.Step(1),
        EditorCommand.StepDown => editor.Step(-1),
        EditorCommand.CrossbarUp => editor.MoveCrossbar(1),
        EditorCommand.CrossbarDown => editor.MoveCrossbar(-1),
        EditorCommand.StaffUp => editor.MoveToStaff(-1),
        EditorCommand.StaffDown => editor.MoveToStaff(1),
        EditorCommand.TieToNext => editor.ToggleTie(),
        EditorCommand.Respell => editor.Respell(),
        EditorCommand.FlipStem => editor.FlipStem(),
        EditorCommand.AutoStem => editor.AutoStem(),
        EditorCommand.NoteC or EditorCommand.NoteD or EditorCommand.NoteE or EditorCommand.NoteF or EditorCommand.NoteG
            or EditorCommand.NoteA or EditorCommand.NoteB => editor.EnterNote(command.ToString()[^1]),
        EditorCommand.AddC or EditorCommand.AddD or EditorCommand.AddE or EditorCommand.AddF or EditorCommand.AddG
            or EditorCommand.AddA or EditorCommand.AddB => editor.AddToChord(command.ToString()[^1]),
        EditorCommand.ChooseWhole => editor.SetInputValue(NoteType.Whole, 0),
        EditorCommand.ChooseHalf => editor.SetInputValue(NoteType.Half, 0),
        EditorCommand.ChooseQuarter => editor.SetInputValue(NoteType.Quarter, 0),
        EditorCommand.ChooseEighth => editor.SetInputValue(NoteType.Eighth, 0),
        EditorCommand.ChooseSixteenth => editor.SetInputValue(NoteType.Sixteenth, 0),
        EditorCommand.ChooseThirtySecond => editor.SetInputValue(NoteType.ThirtySecond, 0),
        EditorCommand.EnterRest => editor.EnterRest(),
        EditorCommand.BreakBeam or EditorCommand.GraceNote or EditorCommand.HideEntry =>
            EditResult.Refused($"'{KeyMap.Info(command).Name}'은(는) 아직 지원하지 않습니다."),
        _ => null,
    };

    private static EditResult Cancel(ScoreEditor editor)
    {
        if (editor.PendingTuplet is { } pending)
        {
            editor.CancelTuplet();
            return EditResult.Ok($"{ScoreEditor.TupletName(pending)} 입력을 취소했습니다.");
        }

        editor.ClearSelection();
        return EditResult.Ok("선택을 해제했습니다.");
    }
}

/// <summary>The tools of the tool palette (docs/KEYS_AND_TOOLS.md, tools). Stored in settings by name.</summary>
public enum EditorTool
{
    Selection,
    Speedy,
    Simple,
    Lyrics,
    Chord,
    KeySignature,
    TimeSignature,
    Clef,
}

public static class EditorTools
{
    /// <summary>The keys a tool uses besides the common ones.</summary>
    public static KeyScope Scope(EditorTool tool) => tool switch
    {
        EditorTool.Speedy => KeyScope.Speedy,
        EditorTool.Simple => KeyScope.Simple,
        _ => KeyScope.Common,
    };

    public static string Name(EditorTool tool) => tool switch
    {
        EditorTool.Selection => "선택 도구",
        EditorTool.Speedy => "Speedy Entry 도구",
        EditorTool.Simple => "Simple Entry 도구",
        EditorTool.Lyrics => "가사 도구",
        EditorTool.Chord => "코드 도구",
        EditorTool.KeySignature => "조표 도구",
        EditorTool.TimeSignature => "박자표 도구",
        _ => "음자리표 도구",
    };

    public static string ScopeName(KeyScope scope) => scope switch
    {
        KeyScope.Speedy => "Speedy Entry",
        KeyScope.Simple => "Simple Entry",
        _ => "공통",
    };
}
