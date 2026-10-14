using ScoreStudio.Editing;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Core.Tests;

/// <summary>Key settings and Speedy Entry (docs/KEYS_AND_TOOLS.md).</summary>
public sealed class KeyMapTests
{
    private static Rational R(long n, long d = 1) => new(n, d);

    private static KeyStroke K(string key, bool ctrl = false, bool shift = false, bool alt = false) => KeyStroke.Of(key, ctrl, shift, alt);

    [Theory]
    [InlineData(KeyScope.Speedy, "D5", false, false, false, EditorCommand.EnterQuarter)]
    [InlineData(KeyScope.Speedy, "NumPad8", false, false, false, EditorCommand.EnterBreve)]
    [InlineData(KeyScope.Speedy, "Return", false, false, false, EditorCommand.AddAtCrossbar)]
    [InlineData(KeyScope.Speedy, "Back", false, false, false, EditorCommand.ToRest)]
    [InlineData(KeyScope.Speedy, "OemPlus", false, true, false, EditorCommand.SemitoneUp)]
    [InlineData(KeyScope.Speedy, "Up", false, false, false, EditorCommand.StepUp)]
    [InlineData(KeyScope.Speedy, "Up", false, false, true, EditorCommand.CrossbarUp)]
    [InlineData(KeyScope.Speedy, "Down", false, true, false, EditorCommand.StaffDown)]
    [InlineData(KeyScope.Speedy, "T", false, false, false, EditorCommand.TieToNext)]
    [InlineData(KeyScope.Speedy, "D9", false, false, false, EditorCommand.Respell)]
    [InlineData(KeyScope.Speedy, "L", true, false, false, EditorCommand.AutoStem)] // the tool's key comes before the common Ctrl+L
    [InlineData(KeyScope.Speedy, "D3", true, false, false, EditorCommand.Tuplet3)]
    [InlineData(KeyScope.Speedy, "Z", true, false, false, EditorCommand.Undo)]
    [InlineData(KeyScope.Simple, "C", false, false, false, EditorCommand.NoteC)]
    [InlineData(KeyScope.Simple, "C", false, true, false, EditorCommand.AddC)]
    [InlineData(KeyScope.Simple, "D5", false, false, false, EditorCommand.ChooseQuarter)]
    [InlineData(KeyScope.Simple, "L", true, false, false, EditorCommand.LyricBox)]
    [InlineData(KeyScope.Common, "Left", false, false, false, EditorCommand.SelectPrevious)]
    public void DefaultKeys_AreFinales(KeyScope tool, string key, bool ctrl, bool shift, bool alt, EditorCommand command) =>
        Assert.Equal(command, KeyMap.Defaults().Find(tool, K(key, ctrl, shift, alt)));

    [Fact]
    public void DefaultKeys_DoNotClash_AndEveryCommandHasANameAndARunner()
    {
        var map = KeyMap.Defaults();
        foreach (var scope in Enum.GetValues<KeyScope>())
        {
            var keys = map.All().Where(b => b.Scope == scope).SelectMany(b => b.Keys).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        Assert.Equal(Enum.GetValues<EditorCommand>().Order(), KeyMap.Commands.Select(c => c.Command).Order());
        Assert.All(KeyMap.Commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        Assert.Null(map.Find(KeyScope.Common, K("D5"))); // value keys belong to the entry tools
        Assert.Null(map.Find(KeyScope.Speedy, K("C")));

        // Every command either runs on the editor or is one the window does.
        var windowCommands = new[] { EditorCommand.LyricBox, EditorCommand.ChordBox, EditorCommand.MouseEntry };
        foreach (var command in Enum.GetValues<EditorCommand>())
            Assert.Equal(windowCommands.Contains(command), CommandRunner.Run(new ScoreEditor(ScoreProject.CreateNew()), command) is null);
    }

    [Fact]
    public void ChangedKeys_TakeOver_ClashingKeysMove_AndOnlyChangesAreSaved()
    {
        var map = KeyMap.Defaults();
        Assert.Equal(EditorCommand.StepUp, map.Add(KeyScope.Speedy, EditorCommand.CrossbarUp, K("Up")));
        Assert.Equal(EditorCommand.CrossbarUp, map.Find(KeyScope.Speedy, K("Up")));
        Assert.Empty(map.KeysFor(KeyScope.Speedy, EditorCommand.StepUp));
        map.Remove(KeyScope.Speedy, EditorCommand.TieToNext, K("T"));
        Assert.Null(map.Find(KeyScope.Speedy, K("T")));
        Assert.Null(map.Add(KeyScope.Simple, EditorCommand.TieToNext, K("T"))); // T was free in Simple Entry

        var changes = map.Changes();
        Assert.Equal(new[] { "Simple:TieToNext", "Speedy:CrossbarUp", "Speedy:StepUp", "Speedy:TieToNext" }, changes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Alt+Up", "Up" }, changes["Speedy:CrossbarUp"].Order(StringComparer.Ordinal));

        // Through the settings file and back.
        var path = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { KeyBindings = changes }.Save(path);
            var reread = KeyMap.WithChanges(AppSettings.Load(path).KeyBindings);
            Assert.Equal(EditorCommand.CrossbarUp, reread.Find(KeyScope.Speedy, K("Up")));
            Assert.Equal(EditorCommand.TieToNext, reread.Find(KeyScope.Simple, K("T")));
            Assert.Null(reread.Find(KeyScope.Speedy, K("T")));
            Assert.Equal(changes.Keys.Order(), reread.Changes().Keys.Order());
        }
        finally
        {
            File.Delete(path);
        }

        map.Reset(KeyScope.Speedy, EditorCommand.StepUp);
        Assert.Equal(EditorCommand.StepUp, map.Find(KeyScope.Speedy, K("Up")));
        Assert.Empty(KeyMap.Defaults().Changes());
    }

    [Fact]
    public void DamagedKeySettings_AreLeftOut()
    {
        var map = KeyMap.WithChanges(new Dictionary<string, List<string>>
        {
            ["Speedy:NoSuchCommand"] = new() { "Q" },
            ["Nowhere:StepUp"] = new() { "Q" },
            ["Speedy:TieToNext"] = new() { "Ctrl+Hyper+Q", "", "Shift+R" },
        });
        Assert.Equal(new[] { K("R", shift: true) }, map.KeysFor(KeyScope.Speedy, EditorCommand.TieToNext));
        Assert.Equal(EditorCommand.StepUp, map.Find(KeyScope.Speedy, K("Up")));
    }

    [Theory]
    [InlineData("Ctrl+Shift+Z", "Z", true, true, false, "Ctrl+Shift+Z")]
    [InlineData("Return", "Enter", false, false, false, "Enter")]
    [InlineData("Alt+Up", "Up", false, false, true, "Alt+↑")]
    [InlineData("D5", "D5", false, false, false, "5")]
    [InlineData("Shift+OemPlus", "OemPlus", false, true, false, "Shift+=")]
    [InlineData("NumPad3", "NumPad3", false, false, false, "숫자판 3")]
    public void KeysAreWrittenAndShown(string text, string key, bool ctrl, bool shift, bool alt, string shown)
    {
        var stroke = KeyStroke.Parse(text);
        Assert.Equal(new KeyStroke(key, ctrl, shift, alt), stroke);
        Assert.Equal(shown, stroke!.Display());
        Assert.Equal(stroke, KeyStroke.Parse(stroke.ToString()));
    }

    // ---- Speedy Entry ----------------------------------------------------------------------------------------------

    private static ScoreEditor Speedy() => new(ScoreProject.CreateNew());

    private static void Run(ScoreEditor editor, params EditorCommand[] commands)
    {
        foreach (var command in commands)
        {
            var result = CommandRunner.Run(editor, command);
            Assert.True(result is { Done: true }, $"{command}: {result?.Message}");
        }
    }

    private static List<(Rational Start, string Kind, int[] Pitches)> Chords(ScoreEditor editor, int measure = 0) =>
        editor.Project.Parts[0].Measures[measure].Events.GroupBy(e => new Rational(e.PositionNumerator, e.PositionDenominator)).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.First().Kind, g.Where(e => e.Kind == "Note").Select(e => e.MidiPitch).Order().ToArray())).ToList();

    [Fact]
    public void Speedy_ValueKeysEnterAtTheCrossbar_WhichArrowsMove()
    {
        var editor = Speedy();
        Assert.Equal(34, editor.Crossbar); // B4, the treble staff's middle line
        Run(editor, EditorCommand.StepUp, EditorCommand.StepUp, EditorCommand.EnterQuarter, EditorCommand.EnterQuarter);
        Assert.Equal(new[] { (R(0), "Note", new[] { 74 }), (R(1), "Note", new[] { 74 }) }, Chords(editor)); // D5 twice
        Assert.Equal("D5", editor.CrossbarName);

        // On a chosen note, a value key changes its value and the arrows move it (in the key).
        Assert.True(editor.Select(new EditPosition(0, 0, 1, 0, R(0))).Done);
        Run(editor, EditorCommand.EnterEighth);
        Assert.Equal(new[] { R(0), R(1, 2), R(1) }, Chords(editor).Select(c => c.Start));
        Run(editor, EditorCommand.StepUp);
        Assert.Equal(76, Chords(editor)[0].Pitches.Single());
        Run(editor, EditorCommand.AddDot);
        Assert.Equal(new[] { R(0), R(3, 4), R(1) }, Chords(editor).Select(c => c.Start));

        // Alt+arrow moves only the crossbar; Enter adds its note to the chord, and again takes it out.
        Run(editor, EditorCommand.CrossbarDown, EditorCommand.CrossbarDown, EditorCommand.AddAtCrossbar);
        Assert.Equal(new[] { 72, 76 }, Chords(editor)[0].Pitches);
        Run(editor, EditorCommand.AddAtCrossbar);
        Assert.Equal(new[] { 76 }, Chords(editor)[0].Pitches);

        // Plus and minus are half steps; Backspace makes the note a rest.
        Run(editor, EditorCommand.SemitoneUp);
        Assert.Equal(77, Chords(editor)[0].Pitches.Single());
        Run(editor, EditorCommand.ToRest);
        Assert.Equal("Rest", Chords(editor)[0].Kind);
    }

    [Fact]
    public void Speedy_TiesRespellsAndStems()
    {
        var editor = Speedy();
        foreach (var m in editor.Project.Parts[0].Measures) m.KeyFifths = 1; // G major: F is F#
        Run(editor, EditorCommand.CrossbarUp, EditorCommand.CrossbarUp, EditorCommand.CrossbarUp, EditorCommand.CrossbarUp); // F#5
        Run(editor, EditorCommand.EnterQuarter, EditorCommand.EnterQuarter);
        var first = new EditPosition(0, 0, 1, 0, R(0));
        editor.Select(first);
        Run(editor, EditorCommand.TieToNext);
        Assert.True(editor.Project.Parts[0].Measures[0].Events.Single(e => e.PositionNumerator == 0).TieToNext);
        Run(editor, EditorCommand.TieToNext);
        Assert.False(editor.Project.Parts[0].Measures[0].Events.Single(e => e.PositionNumerator == 0).TieToNext);

        Run(editor, EditorCommand.Respell);
        Assert.Equal("Gb5", editor.Project.Parts[0].Measures[0].Events.Single(e => e.PositionNumerator == 0).NotationSpelling);
        Run(editor, EditorCommand.Respell);
        Assert.Equal("F#5", editor.Project.Parts[0].Measures[0].Events.Single(e => e.PositionNumerator == 0).NotationSpelling);

        // F#5 sits above the middle line: stems go down by rule, so flipping writes "up"; Ctrl+L clears it.
        Run(editor, EditorCommand.FlipStem);
        Assert.True(editor.Project.Parts[0].Measures[0].Events.Single(e => e.PositionNumerator == 0).StemUp);
        Run(editor, EditorCommand.AutoStem);
        Assert.Null(editor.Project.Parts[0].Measures[0].Events.Single(e => e.PositionNumerator == 0).StemUp);
        Assert.False(editor.AutoStem().Done);

        // A note followed by another pitch cannot be tied; D, G and A have no other single-accidental name.
        editor.Select(new EditPosition(0, 0, 1, 0, R(1)));
        Run(editor, EditorCommand.StepDown, EditorCommand.StepDown); // F#5 → D5
        editor.Select(first);
        Assert.False(editor.ToggleTie().Done);
        editor.Select(new EditPosition(0, 0, 1, 0, R(1)));
        Assert.False(editor.Respell().Done);
    }

    [Fact]
    public void Speedy_StaffKeysMoveAcrossStavesAndParts_AndTheCrossbarIsDrawn()
    {
        var project = ScoreProject.CreateNew();
        project.Parts[0].Staves.Add(new ScoreStaff { Clef = ClefType.Bass });
        var editor = new ScoreEditor(project);
        Run(editor, EditorCommand.StaffDown);
        Assert.Equal((0, 1), (editor.Cursor.Part, editor.Cursor.Staff));
        Assert.Equal(22, editor.Crossbar); // D3, the bass staff's middle line
        Assert.False(editor.MoveToStaff(1).Done);
        Run(editor, EditorCommand.StaffUp);
        Assert.Equal(0, editor.Cursor.Staff);

        // The crossbar is a bar at the cursor with the leger lines it needs (middle C: one).
        editor.SetCrossbar(28);
        var layout = ScoreLayoutEngine.Layout(project);
        var mark = CrossbarMark.Find(layout, project, editor.Cursor, editor.Crossbar);
        Assert.NotNull(mark);
        var staffTop = layout.Pages[0].Systems[0].Staves[0].Top;
        var lines = mark.Value.Items.OfType<LineItem>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(staffTop + 5 * layout.StaffSpace, l.Y1, 6));

        // A click on the staff gives the crossbar its height: the treble staff's bottom line is E4, the bass staff's middle line D3.
        var system = layout.Pages[0].Systems[0];
        var measure = system.Measures[0];
        var x = (measure.Left + measure.Right) / 2;
        Assert.Equal(30, MouseEntry.DiatonicAt(layout, 0, project, x, staffTop + 4 * layout.StaffSpace));
        Assert.Equal(22, MouseEntry.DiatonicAt(layout, 0, project, x, system.Staves[1].Top + 2 * layout.StaffSpace));
        Assert.Null(MouseEntry.DiatonicAt(layout, 0, project, x, 2));
    }

    [Fact]
    public void Simple_ArrowsMoveByStep_AndPlusMinusByHalfStep()
    {
        var editor = Speedy();
        Run(editor, EditorCommand.NoteF);
        Run(editor, EditorCommand.StepUp);
        Assert.Equal(67, Chords(editor)[0].Pitches.Single()); // F4 (nearest the middle line) → G4
        Run(editor, EditorCommand.SemitoneDown);
        Assert.Equal(66, Chords(editor)[0].Pitches.Single());
    }
}
