using System.Globalization;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;

namespace ScoreStudio.Editing;

/// <summary>Speedy Entry and the note commands Finale has keys for (docs/KEYS_AND_TOOLS.md).</summary>
public sealed partial class ScoreEditor
{
    private int? _crossbar;

    /// <summary>
    /// Speedy Entry's crossbar: where the next note goes, as a diatonic step number from C0. It starts on the staff's
    /// middle line and follows the notes entered or chosen.
    /// </summary>
    public int Crossbar => _crossbar ?? MiddleLine(Cursor);

    private int MiddleLine(EditPosition at) =>
        IsValid(at with { Position = Rational.Zero })
            ? Notation.BottomLineDiatonic(new ClefTimeline(Project).ClefAt(at.Part, at.Staff, at.Measure, at.Position)) + 4
            : 34; // B4

    /// <summary>The crossbar's pitch name in the key at the cursor, e.g. "F#5".</summary>
    public string CrossbarName => PitchName(Crossbar, IsValid(Cursor with { Position = Rational.Zero }) ? MeasureAt(Cursor).KeyFifths : 0);

    private static string PitchName(int diatonic, int keyFifths)
    {
        var alter = Notation.KeyAlter(keyFifths, diatonic % 7);
        return $"{StepLetters[diatonic % 7]}{(alter > 0 ? new string('#', alter) : new string('b', -alter))}{(diatonic / 7).ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Puts the crossbar at a height (e.g. where the staff was clicked).</summary>
    public void SetCrossbar(int diatonic) => _crossbar = Math.Clamp(diatonic, 0, 74);

    /// <summary>Moves the crossbar by lines and spaces, leaving the notes alone.</summary>
    public EditResult MoveCrossbar(int steps)
    {
        SetCrossbar(Crossbar + steps);
        return EditResult.Ok($"입력 높이: {CrossbarName}");
    }

    private void FollowChord(EditPosition at)
    {
        if (ChordAt(at) is { } chord && chord[0].Kind == "Note")
            _crossbar = chord.Max(e => Notation.Spell(e, MeasureAt(at).KeyFifths).Diatonic);
    }

    /// <summary>
    /// A Speedy Entry value key: changes the chosen note's value (not the one just entered), or enters a note of that value
    /// at the crossbar.
    /// </summary>
    public EditResult EnterAtCrossbar(NoteType type)
    {
        if (Selection is { } chosen && !SelectionIsEntry && ChordAt(chosen) is not null) return SetInputValue(type, 0);
        if (!CursorIsValid()) return EditResult.Refused("입력할 위치가 없습니다. 악보에서 마디를 먼저 누르세요.");
        InputType = type;
        InputDots = 0;
        return EnterNoteAt(Cursor, Crossbar);
    }

    /// <summary>Adds or removes a dot on the selected note (the one just entered too), or on the input value.</summary>
    public EditResult AddDot()
    {
        if (Selection is not { } at || ChordAt(at) is not { } chord) return ToggleDot();
        var measure = MeasureAt(at);
        var value = Notation.ValueOf(Duration(chord[0]), TupletRatioAt(measure, at.Staff, at.Voice, at.Position));
        if (!value.IsExact) return EditResult.Refused("이 음표의 길이에는 점을 붙일 수 없습니다.");
        SelectionIsEntry = false;
        var result = SetInputValue(value.Type, value.Dots == 0 ? 1 : 0);
        SelectionIsEntry = true;
        return result;
    }

    /// <summary>
    /// Up or down by lines and spaces: the selected notes (in the key, with the notes tied to them), or else the crossbar.
    /// </summary>
    public EditResult Step(int steps)
    {
        if (Selection is not { } at || ChordAt(at) is not { } chord || chord[0].Kind != "Note") return MoveCrossbar(steps);
        return Change(() =>
        {
            var key = MeasureAt(at).KeyFifths;
            var events = TiedWith(at, ChordAt(at)!);
            var moved = new List<(ScoreEvent Event, ScoreEvent Note)>();
            foreach (var e in events)
            {
                var diatonic = Notation.Spell(e, key).Diatonic + steps;
                if (diatonic < 0 || NoteAt(at, diatonic / 7, diatonic % 7, key) is not { } note) return "음높이 범위를 벗어납니다.";
                moved.Add((e, note));
            }

            foreach (var (e, note) in moved) (e.MidiPitch, e.NotationSpelling) = (note.MidiPitch, note.NotationSpelling);
            FollowChord(at);
            return null;
        }, () => $"{Math.Abs(steps)}칸 {(steps > 0 ? "올렸습니다" : "내렸습니다")}: {CrossbarName}");
    }

    /// <summary>
    /// Speedy Entry's Enter: adds the crossbar's note to the selected chord (a rest becomes that note, an empty place gets
    /// it), or takes it out when the chord already has it.
    /// </summary>
    public EditResult AddAtCrossbar()
    {
        var at = Selection ?? Cursor;
        if (!IsValid(at)) return EditResult.Refused("음을 더할 자리를 먼저 누르세요.");
        if (ChordAt(at) is { } chord && chord[0].Kind == "Note")
        {
            var key = MeasureAt(at).KeyFifths;
            if (chord.FirstOrDefault(e => Notation.Spell(e, key).Diatonic == Crossbar) is { } existing)
            {
                if (chord.Count == 1) return EditResult.Refused("화음의 마지막 음은 뺄 수 없습니다. 쉼표로 바꾸려면 Backspace를 누르세요.");
                var pitch = existing.MidiPitch;
                return Change(() =>
                {
                    var target = ChordAt(at)!.First(e => e.MidiPitch == pitch);
                    var measure = MeasureAt(at);
                    var keep = ChordAt(at)!.First(e => e != target);
                    if (keep.Lyrics.Count == 0) (keep.Lyrics, keep.ExtendEnds) = (target.Lyrics, target.ExtendEnds);
                    measure.Events.Remove(target);
                    return null;
                }, () => $"화음에서 {CrossbarName}을(를) 뺐습니다.");
            }
        }

        return AddToChordAt(at, Crossbar);
    }

    /// <summary>Moves the cursor to the staff above or below (across parts), at the same time where possible.</summary>
    public EditResult MoveToStaff(int direction)
    {
        var staves = Project.Parts.SelectMany((part, p) => Enumerable.Range(0, part.Staves.Count).Select(s => (Part: p, Staff: s))).ToList();
        var index = staves.IndexOf((Cursor.Part, Cursor.Staff)) + direction;
        if (index < 0 || index >= staves.Count) return EditResult.Refused(direction < 0 ? "위에 보표가 없습니다." : "아래에 보표가 없습니다.");

        var (part, staff) = staves[index];
        var measures = Project.Parts[part].Measures;
        if (measures.Count == 0) return EditResult.Refused("그 파트에는 마디가 없습니다.");
        var measure = Math.Min(Cursor.Measure, measures.Count - 1);
        var position = Cursor.Position < MeasureLength(measures[measure]) ? Cursor.Position : Rational.Zero;
        Cursor = new EditPosition(part, staff, 1, measure, position);
        ClearSelection();
        _crossbar = null;
        return EditResult.Ok($"{Project.Parts[part].Name}{(Project.Parts[part].Staves.Count > 1 ? $" {staff + 1}번째 보표" : "")}로 옮겼습니다.");
    }

    /// <summary>Ties the selected notes to the same pitches of the next chord in the voice, or unties them when all are tied.</summary>
    public EditResult ToggleTie()
    {
        if (Selection is not { } at || ChordAt(at) is not { } chord || chord[0].Kind != "Note") return EditResult.Refused("붙임줄을 걸 음표를 먼저 선택하세요.");
        var chords = VoiceThroughout(at);
        var index = chords.FindIndex(c => c.At == at);
        var next = index + 1 < chords.Count ? chords[index + 1].Chord : null;
        if (chord.All(e => e.TieToNext))
        {
            return Change(() =>
            {
                foreach (var e in ChordAt(at)!) e.TieToNext = false;
                return null;
            }, () => "붙임줄을 뺐습니다.");
        }

        if (next is null || !chord.Any(e => next.Any(n => n.Kind == "Note" && n.MidiPitch == e.MidiPitch)))
            return EditResult.Refused("다음 음이 같은 높이가 아니라 붙임줄을 걸 수 없습니다.");
        var pitches = next.Where(n => n.Kind == "Note").Select(n => n.MidiPitch).ToHashSet();
        return Change(() =>
        {
            foreach (var e in ChordAt(at)!.Where(e => pitches.Contains(e.MidiPitch))) e.TieToNext = true;
            return null;
        }, () => "다음 음과 붙임줄로 이었습니다.");
    }

    /// <summary>Writes the selected notes with their other names: F♯ ↔ G♭, E ↔ F♭, B ↔ C♭, F ↔ E♯, C ↔ B♯.</summary>
    public EditResult Respell()
    {
        if (Selection is not { } at || ChordAt(at) is not { } chord || chord[0].Kind != "Note") return EditResult.Refused("이름을 바꿀 음표를 먼저 선택하세요.");
        return Change(() =>
        {
            var key = MeasureAt(at).KeyFifths;
            var changed = 0;
            foreach (var e in ChordAt(at)!)
            {
                var spelled = Notation.Spell(e, key);
                var direction = spelled.Alter > 0 || (spelled.Alter == 0 && spelled.Step is 2 or 6) ? 1
                    : spelled.Alter < 0 || (spelled.Alter == 0 && spelled.Step is 0 or 3) ? -1 : 0;
                if (direction == 0) continue;
                var diatonic = spelled.Diatonic + direction;
                var (step, octave) = (diatonic % 7, diatonic / 7);
                var alter = e.MidiPitch - (12 * (octave + 1) + StepSemitones[step]);
                if (Math.Abs(alter) > 2) continue;
                e.NotationSpelling = $"{StepLetters[step]}{(alter > 0 ? new string('#', alter) : new string('b', -alter))}{octave.ToString(CultureInfo.InvariantCulture)}";
                changed++;
            }

            return changed == 0 ? "다른 이름으로 쓸 수 없는 음입니다(D, G, A)." : null;
        }, () => "음 이름을 바꿨습니다(딴이름한소리).");
    }

    /// <summary>Turns the selected chord's stem the other way (and keeps it so).</summary>
    public EditResult FlipStem()
    {
        if (Selection is not { } at || ChordAt(at) is not { } chord || chord[0].Kind != "Note") return EditResult.Refused("기둥 방향을 바꿀 음표를 먼저 선택하세요.");
        return Change(() =>
        {
            var events = ChordAt(at)!;
            var up = events[0].StemUp ?? StemsUpByRule(at, events);
            foreach (var e in events) e.StemUp = !up;
            return null;
        }, () => "기둥 방향을 바꿨습니다.");
    }

    /// <summary>Lets the layout choose the selected chord's stem direction again.</summary>
    public EditResult AutoStem()
    {
        if (Selection is not { } at || ChordAt(at) is not { } chord || chord[0].Kind != "Note") return EditResult.Refused("음표를 먼저 선택하세요.");
        if (chord.All(e => e.StemUp is null)) return EditResult.Refused("이미 기둥 방향이 자동입니다.");
        return Change(() =>
        {
            foreach (var e in ChordAt(at)!) e.StemUp = null;
            return null;
        }, () => "기둥 방향을 자동으로 돌렸습니다.");
    }

    /// <summary>The usual rule: stems go up when the notes sit below the middle line on average.</summary>
    private bool StemsUpByRule(EditPosition at, List<ScoreEvent> chord)
    {
        var clef = new ClefTimeline(Project).ClefAt(at.Part, at.Staff, at.Measure, at.Position);
        var key = MeasureAt(at).KeyFifths;
        return chord.Average(e => Notation.StaffPosition(Notation.Spell(e, key), clef)) < 4;
    }
}
