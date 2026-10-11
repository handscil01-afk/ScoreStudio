using System.Globalization;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Editing;

/// <summary>A place in the score: a voice of a staff of a part, a measure (index from 0), and a time in it in quarter notes.</summary>
public sealed record EditPosition(int Part, int Staff, int Voice, int Measure, Rational Position);

/// <summary>The outcome of an edit: whether the score changed, and a message for the status bar.</summary>
public sealed record EditResult(bool Done, string Message)
{
    public static EditResult Ok(string message) => new(true, message);

    public static EditResult Refused(string message) => new(false, message);
}

/// <summary>
/// Note entry and basic editing on a project, with undo and redo. All rules live here so they can be tested without a
/// window; the WPF window only turns keys and clicks into these calls. See docs/SPRINT4_EDITING.md.
/// </summary>
public sealed partial class ScoreEditor
{
    private const int MaxUndo = 200;
    private const string StepLetters = "CDEFGAB";
    private static readonly int[] StepSemitones = { 0, 2, 4, 5, 7, 9, 11 };

    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    private (List<ScoreEvent> Events, Rational Length)? _clipboard;

    public ScoreEditor(ScoreProject project)
    {
        Project = project;
        Cursor = new EditPosition(0, 0, 1, 0, Rational.Zero);
    }

    public ScoreProject Project { get; private set; }

    /// <summary>Where the next note goes.</summary>
    public EditPosition Cursor { get; private set; }

    /// <summary>The selected chord or rest (its voice and start), or null.</summary>
    public EditPosition? Selection { get; private set; }

    /// <summary>
    /// True when the selection is the chord just entered: a duration key then sets the next note's length only, instead of
    /// changing the entered note.
    /// </summary>
    public bool SelectionIsEntry { get; private set; }

    public NoteType InputType { get; private set; } = NoteType.Quarter;

    /// <summary>
    /// A note longer than what is left of the measure: false refuses it (the default); true enters what fits and goes on
    /// in the next measures, tied (rests are split without ties), adding measures at the end as needed.
    /// </summary>
    public bool TieAcrossBarlines { get; set; }

    public int InputDots { get; private set; }

    public Rational InputLength => Length(InputType, InputDots);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool CanPaste => _clipboard is not null;

    /// <summary>Goes up whenever the score changes (an edit, an undo or a redo); selection moves do not count.</summary>
    public int ChangeCount { get; private set; }

    // ---- Entry ----------------------------------------------------------------------------------------------------

    /// <summary>Enters a note named by its letter (C to B) at the cursor, in the key, nearest to the previous note.</summary>
    public EditResult EnterNote(char letter)
    {
        var step = StepLetters.IndexOf(char.ToUpperInvariant(letter));
        if (step < 0) return EditResult.Refused($"'{letter}'은(는) 음 이름이 아닙니다.");
        return Enter(step);
    }

    /// <summary>Enters a rest at the cursor.</summary>
    public EditResult EnterRest() => Enter(null);

    /// <summary>
    /// Enters a note of an exact written pitch (diatonic step number from C0, altered by the key) at a place, e.g. where
    /// the mouse was clicked; the cursor moves there first.
    /// </summary>
    public EditResult EnterNoteAt(EditPosition at, int diatonic)
    {
        if (!IsValid(at)) return EditResult.Refused("그 자리에 입력할 수 없습니다.");
        if (diatonic < 0) return EditResult.Refused("입력할 수 있는 음높이 범위를 벗어났습니다.");
        return Enter(diatonic % 7, diatonic, at);
    }

    private EditResult Enter(int? step, int? diatonic = null, EditPosition? where = null)
    {
        if (where is null && !CursorIsValid()) return EditResult.Refused("입력할 위치가 없습니다. 악보에서 마디를 먼저 누르세요.");

        var at = where ?? Cursor;
        var length = InputLength;
        string? pitchName = null;
        return Change(() =>
        {
            var measure = MeasureAt(at);
            var available = MeasureLength(measure) - at.Position;
            if (length > available && !TieAcrossBarlines)
                return $"이 마디에 남은 길이({DescribeLength(available)})보다 긴 {DescribeValue(InputType, InputDots, step is null)}는 넣을 수 없습니다. "
                    + "(설정에서 '마디를 넘는 음표를 다음 마디로 이어 넣기'를 켜면 붙임줄로 이어 넣습니다.)";

            ScoreEvent? note = null;
            if (step is { } s)
            {
                note = diatonic is { } d ? NoteAt(at, d / 7, s, measure.KeyFifths) : MakeNote(at, s, ReferenceDiatonic(at), measure.KeyFifths);
                if (note is null) return "입력할 수 있는 음높이 범위를 벗어났습니다.";
                pitchName = note.NotationSpelling;
            }

            // Measure by measure: what fits here, written as plain values, tied on to the rest in the next measure.
            var place = at;
            var remaining = length;
            while (true)
            {
                var current = MeasureAt(place);
                var take = Min(remaining, MeasureLength(current) - place.Position);
                ClearSpan(current, place.Staff, place.Voice, place.Position, place.Position + take);
                var pieces = take == length ? new List<Rational> { take } : Pieces(take);
                var position = place.Position;
                foreach (var piece in pieces)
                {
                    remaining -= piece;
                    if (note is null)
                    {
                        current.Events.Add(NewRest(place, position, piece));
                    }
                    else
                    {
                        var copy = Clone(new[] { note })[0];
                        copy.TieToNext = remaining.Sign > 0;
                        current.Events.Add(WithTime(copy, position, piece));
                    }

                    position += piece;
                }

                AutoBeam(current, place.Staff, place.Voice);
                var lastMeasure = place.Measure == Project.Parts[place.Part].Measures.Count - 1;
                if (remaining.Sign > 0)
                {
                    if (lastMeasure) AddMeasureAfter(place.Measure);
                    place = place with { Measure = place.Measure + 1, Position = Rational.Zero };
                    continue;
                }

                if (position < MeasureLength(current))
                {
                    Cursor = place with { Position = position };
                }
                else
                {
                    if (lastMeasure) AddMeasureAfter(place.Measure);
                    Cursor = place with { Measure = place.Measure + 1, Position = Rational.Zero };
                }

                break;
            }

            Selection = at;
            SelectionIsEntry = true;
            return null;
        }, () => step is null
            ? $"{DescribeValue(InputType, InputDots, rest: true)}를 넣었습니다."
            : $"{pitchName} {DescribeValue(InputType, InputDots, rest: false)}를 넣었습니다.");
    }

    /// <summary>
    /// Adds a note named by its letter above the selected chord's highest note; a selected rest becomes that note.
    /// </summary>
    public EditResult AddToChord(char letter)
    {
        var step = StepLetters.IndexOf(char.ToUpperInvariant(letter));
        if (step < 0) return EditResult.Refused($"'{letter}'은(는) 음 이름이 아닙니다.");
        if (Selection is not { } at || ChordAt(at) is not { Count: > 0 } chord) return EditResult.Refused("음을 더할 화음을 먼저 선택하세요.");

        string? name = null;
        return Change(() =>
        {
            var measure = MeasureAt(at);
            var events = ChordAt(at)!;
            var start = Start(events[0]);
            var length = Duration(events[0]);
            if (events[0].Kind == "Rest")
            {
                var note = MakeNote(at, step, ReferenceDiatonic(at), measure.KeyFifths);
                if (note is null) return "입력할 수 있는 음높이 범위를 벗어났습니다.";
                measure.Events.Remove(events[0]);
                measure.Events.Add(WithTime(note, start, length));
                name = note.NotationSpelling;
            }
            else
            {
                var top = events.OrderBy(e => e.MidiPitch).Last();
                var topDiatonic = Notation.Spell(top, measure.KeyFifths).Diatonic;
                var diatonic = topDiatonic + ((step - topDiatonic % 7 + 7) % 7);
                if (diatonic == topDiatonic) diatonic += 7;
                var note = NoteAt(at, diatonic / 7, step, measure.KeyFifths);
                if (note is null) return "입력할 수 있는 음높이 범위를 벗어났습니다.";
                if (events.Any(e => e.MidiPitch == note.MidiPitch)) return "화음에 이미 있는 음입니다.";
                note.Beams = new List<BeamType>(top.Beams);
                note.StemUp = top.StemUp;
                measure.Events.Add(WithTime(note, start, length));
                name = note.NotationSpelling;
            }

            AutoBeam(measure, at.Staff, at.Voice);
            return null;
        }, () => $"화음에 {name}을(를) 더했습니다.");
    }

    /// <summary>
    /// Adds a note of an exact written pitch to the chord at a place (Shift+click); a rest there becomes that note, and
    /// an empty place gets the note as a new entry.
    /// </summary>
    public EditResult AddToChordAt(EditPosition at, int diatonic)
    {
        if (!IsValid(at)) return EditResult.Refused("그 자리에 입력할 수 없습니다.");
        if (diatonic < 0) return EditResult.Refused("입력할 수 있는 음높이 범위를 벗어났습니다.");
        if (ChordAt(at) is not { Count: > 0 }) return EnterNoteAt(at, diatonic);

        string? name = null;
        return Change(() =>
        {
            var measure = MeasureAt(at);
            var events = ChordAt(at)!;
            var note = NoteAt(at, diatonic / 7, diatonic % 7, measure.KeyFifths);
            if (note is null) return "입력할 수 있는 음높이 범위를 벗어났습니다.";
            if (events.Any(e => e.Kind == "Note" && e.MidiPitch == note.MidiPitch)) return "화음에 이미 있는 음입니다.";

            if (events[0].Kind == "Rest")
            {
                measure.Events.Remove(events[0]);
            }
            else
            {
                note.Beams = new List<BeamType>(events[0].Beams);
                note.StemUp = events[0].StemUp;
            }

            measure.Events.Add(WithTime(note, Start(events[0]), Duration(events[0])));
            AutoBeam(measure, at.Staff, at.Voice);
            Selection = at;
            SelectionIsEntry = true;
            var after = Start(events[0]) + Duration(events[0]);
            Cursor = after < MeasureLength(measure) ? at with { Position = after }
                : at.Measure + 1 < Project.Parts[at.Part].Measures.Count ? at with { Measure = at.Measure + 1, Position = Rational.Zero }
                : at;
            name = note.NotationSpelling;
            return null;
        }, () => $"화음에 {name}을(를) 더했습니다.");
    }

    // ---- Changing the selection ----------------------------------------------------------------------------------

    /// <summary>Moves every note of the selected chord by <paramref name="semitones"/>.</summary>
    public EditResult Transpose(int semitones)
    {
        if (Selection is not { } at || ChordAt(at) is not { Count: > 0 } chord || chord[0].Kind == "Rest")
            return EditResult.Refused("음높이를 바꿀 음표를 먼저 선택하세요.");

        return Change(() =>
        {
            // Notes tied to the selected ones (before and after, across barlines) move with them, so the ties hold.
            var events = TiedWith(at, ChordAt(at)!);
            if (events.Any(e => e.MidiPitch + semitones is < 0 or > 127)) return "음높이 범위(MIDI 0~127)를 벗어납니다.";

            foreach (var e in events)
            {
                e.MidiPitch += semitones;
                e.NotationSpelling = null; // spelled again from the key, as for imported notes without a spelling
            }

            return null;
        }, () => semitones % 12 == 0
            ? $"{Math.Abs(semitones / 12)}옥타브 {(semitones > 0 ? "올렸습니다" : "내렸습니다")}."
            : $"반음 {Math.Abs(semitones)}개 {(semitones > 0 ? "올렸습니다" : "내렸습니다")}.");
    }

    /// <summary>
    /// Sets the length for the next note. With a chosen selection (not the note just entered) it changes that note too:
    /// shorter leaves a rest, longer takes the following rests or empty time of the voice.
    /// </summary>
    public EditResult SetInputValue(NoteType type, int dots)
    {
        if (type is < NoteType.Whole or > NoteType.SixtyFourth || dots is < 0 or > 2)
            return EditResult.Refused("지원하지 않는 길이입니다.");

        InputType = type;
        InputDots = dots;
        if (Selection is not { } at || SelectionIsEntry || ChordAt(at) is not { Count: > 0 })
            return EditResult.Ok($"입력 길이: {DescribeValue(type, dots, rest: false)}");

        var length = Length(type, dots);
        var isRest = ChordAt(at)![0].Kind == "Rest";
        return Change(() =>
        {
            var measure = MeasureAt(at);
            var events = ChordAt(at)!;
            var old = Duration(events[0]);
            if (length == old) return "이미 그 길이입니다.";
            if (at.Position + length > MeasureLength(measure)) return "마디 끝을 넘어 늘릴 수 없습니다.";

            if (length < old)
            {
                foreach (var e in events) SetDuration(e, length);
                AddRests(measure, at, at.Position + length, at.Position + old);
            }
            else
            {
                var span = VoiceChords(measure, at.Staff, at.Voice)
                    .Where(c => Start(c[0]) >= at.Position + old && Start(c[0]) < at.Position + length);
                if (span.Any(c => c[0].Kind != "Rest")) return "뒤에 음표가 있어 늘릴 수 없습니다. 뒤 음표를 먼저 쉼표로 바꾸세요.";
                ClearSpan(measure, at.Staff, at.Voice, at.Position + old, at.Position + length);
                foreach (var e in events) SetDuration(e, length);
            }

            AutoBeam(measure, at.Staff, at.Voice);
            return null;
        }, () => $"길이를 {DescribeValue(type, dots, isRest)}로 바꿨습니다.");
    }

    /// <summary>Adds or removes a dot on the input length (and on a chosen selection).</summary>
    public EditResult ToggleDot() => SetInputValue(InputType, InputDots == 0 ? 1 : 0);

    /// <summary>Turns the selected chord into a rest of the same length.</summary>
    public EditResult DeleteSelection()
    {
        if (Selection is not { } at || ChordAt(at) is not { Count: > 0 } chord) return EditResult.Refused("지울 음표를 먼저 선택하세요.");
        if (chord[0].Kind == "Rest") return EditResult.Refused("쉼표는 지울 것이 없습니다.");

        return Change(() =>
        {
            var measure = MeasureAt(at);
            var events = ChordAt(at)!;
            UntieInto(at, events);
            foreach (var e in events) measure.Events.Remove(e);
            measure.Events.Add(NewRest(at, Start(events[0]), Duration(events[0])));
            AutoBeam(measure, at.Staff, at.Voice);
            return null;
        }, () => "음표를 쉼표로 바꿨습니다.");
    }

    // ---- Selection and cursor ------------------------------------------------------------------------------------

    /// <summary>Selects the chord or rest at <paramref name="at"/> (e.g. clicked) and puts the cursor on it.</summary>
    public EditResult Select(EditPosition at)
    {
        if (ChordAt(at) is not { Count: > 0 } chord) return EditResult.Refused("그 자리에 음표가 없습니다.");
        Selection = at;
        Cursor = at;
        SelectionIsEntry = false;
        return EditResult.Ok($"{at.Measure + 1}마디 {DescribeBeat(at.Position)}의 {(chord[0].Kind == "Rest" ? "쉼표" : "음표")}를 선택했습니다.");
    }

    /// <summary>Puts the cursor at a staff, measure and time (voice 1) and clears the selection.</summary>
    public EditResult PlaceCursor(int part, int staff, int measure, Rational position)
    {
        var target = new EditPosition(part, staff, 1, measure, position);
        if (!IsValid(target)) return EditResult.Refused("그 자리에 입력할 수 없습니다.");
        Cursor = target;
        Selection = null;
        SelectionIsEntry = false;
        return EditResult.Ok($"입력 위치: {measure + 1}마디 {DescribeBeat(position)}");
    }

    public void ClearSelection()
    {
        Selection = null;
        SelectionIsEntry = false;
    }

    public EditResult SelectNext() => Step(+1);

    public EditResult SelectPrevious() => Step(-1);

    private EditResult Step(int direction)
    {
        var origin = Selection ?? Cursor;
        if (!IsValid(origin with { Position = Rational.Zero })) return EditResult.Refused("선택할 음표가 없습니다.");

        var part = Project.Parts[origin.Part];
        var all = Enumerable.Range(0, part.Measures.Count)
            .SelectMany(m => VoiceChords(part.Measures[m], origin.Staff, origin.Voice).Select(c => (Measure: m, Start: Start(c[0]))))
            .ToList();
        int Compare((int Measure, Rational Start) c) => c.Measure != origin.Measure ? c.Measure.CompareTo(origin.Measure) : c.Start.CompareTo(origin.Position);

        (int Measure, Rational Start)? target = direction > 0
            ? all.Where(c => Selection is null ? Compare(c) >= 0 : Compare(c) > 0).Select(c => ((int, Rational)?)c).FirstOrDefault()
            : all.Where(c => Compare(c) < 0).Select(c => ((int, Rational)?)c).LastOrDefault();
        if (target is not { } t) return EditResult.Refused(direction > 0 ? "뒤에 음표가 없습니다." : "앞에 음표가 없습니다.");
        return Select(origin with { Measure = t.Measure, Position = t.Start });
    }

    // ---- Measures ------------------------------------------------------------------------------------------------

    /// <summary>Inserts an empty measure after the cursor's measure in every part.</summary>
    public EditResult InsertMeasureAfter()
    {
        if (!CursorIsValid()) return EditResult.Refused("마디를 먼저 누르세요.");
        var index = Cursor.Measure;
        return Change(() =>
        {
            AddMeasureAfter(index);
            Cursor = Cursor with { Measure = index + 1, Position = Rational.Zero };
            ClearSelection();
            return null;
        }, () => $"{index + 1}마디 뒤에 마디를 넣었습니다.");
    }

    /// <summary>Deletes the cursor's measure from every part; a line or page break on it moves to the next measure.</summary>
    public EditResult DeleteMeasure()
    {
        if (!CursorIsValid()) return EditResult.Refused("마디를 먼저 누르세요.");
        if (Project.Parts.Max(p => p.Measures.Count) <= 1) return EditResult.Refused("마디가 하나뿐이라 지울 수 없습니다.");
        var index = Cursor.Measure;
        return Change(() =>
        {
            foreach (var part in Project.Parts.Where(p => index < p.Measures.Count))
            {
                var removed = part.Measures[index];
                part.Measures.RemoveAt(index);
                if (index < part.Measures.Count)
                {
                    var next = part.Measures[index];
                    if (!next.SystemBreak && (removed.SystemBreak || removed.PageBreak))
                    {
                        (next.SystemBreak, next.PageBreak) = (true, removed.PageBreak);
                        (next.SystemDistance, next.TopSystemDistance) = (removed.SystemDistance, removed.TopSystemDistance);
                        next.StaffDistances = removed.StaffDistances;
                    }

                    // The clef in force at the end of the deleted measure carries on into the next one.
                    foreach (var last in removed.ClefChanges.GroupBy(c => c.Staff).Select(g => g.OrderBy(c => (double)c.PositionNumerator / c.PositionDenominator).Last()))
                    {
                        if (!next.ClefChanges.Any(c => c.Staff == last.Staff && c.PositionNumerator == 0))
                            next.ClefChanges.Add(new ScoreClefChange { Staff = last.Staff, Clef = last.Clef });
                    }
                }

                for (var m = index; m < part.Measures.Count; m++) part.Measures[m].Number--;
            }

            Cursor = Cursor with { Measure = Math.Min(index, Project.Parts[Cursor.Part].Measures.Count - 1), Position = Rational.Zero };
            ClearSelection();
            return null;
        }, () => $"{index + 1}마디를 지웠습니다.");
    }

    /// <summary>Copies the cursor's measure of the cursor's part (every staff and voice).</summary>
    public EditResult CopyMeasure()
    {
        if (!CursorIsValid()) return EditResult.Refused("복사할 마디를 먼저 누르세요.");
        var measure = MeasureAt(Cursor);
        _clipboard = (Clone(measure.Events), MeasureLength(measure));
        return EditResult.Ok($"{Cursor.Measure + 1}마디를 복사했습니다.");
    }

    /// <summary>Replaces the cursor's measure of the cursor's part with the copied one (same length only).</summary>
    public EditResult PasteMeasure()
    {
        if (_clipboard is not { } clip) return EditResult.Refused("먼저 마디를 복사하세요.");
        if (!CursorIsValid()) return EditResult.Refused("붙여 넣을 마디를 먼저 누르세요.");
        var index = Cursor.Measure;
        return Change(() =>
        {
            var part = Project.Parts[Cursor.Part];
            var measure = part.Measures[index];
            if (MeasureLength(measure) != clip.Length) return "박자가 다른 마디에는 붙여 넣을 수 없습니다.";
            if (clip.Events.Any(e => e.Staff >= part.Staves.Count)) return "보표 수가 다른 파트에는 붙여 넣을 수 없습니다.";

            measure.Events = Clone(clip.Events);
            ClearSelection();
            return null;
        }, () => $"{index + 1}마디에 붙여 넣었습니다.");
    }

    // ---- Undo ----------------------------------------------------------------------------------------------------

    public EditResult Undo()
    {
        if (_undo.Count == 0) return EditResult.Refused("되돌릴 편집이 없습니다.");
        _redo.Add(Capture());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        ChangeCount++;
        return EditResult.Ok("실행 취소했습니다.");
    }

    public EditResult Redo()
    {
        if (_redo.Count == 0) return EditResult.Refused("다시 실행할 편집이 없습니다.");
        _undo.Add(Capture());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        ChangeCount++;
        return EditResult.Ok("다시 실행했습니다.");
    }

    private sealed record Snapshot(string Json, EditPosition Cursor, EditPosition? Selection, bool SelectionIsEntry);

    private Snapshot Capture() => new(ProjectFileService.ToJson(Project), Cursor, Selection, SelectionIsEntry);

    private void Restore(Snapshot snapshot)
    {
        Project = ProjectFileService.FromJson(snapshot.Json);
        (Cursor, Selection, SelectionIsEntry) = (snapshot.Cursor, snapshot.Selection, snapshot.SelectionIsEntry);
    }

    /// <summary>
    /// Runs an edit with an undo snapshot. <paramref name="apply"/> returns why it refused, or null when done; a refused
    /// edit leaves the score, cursor and selection exactly as they were.
    /// </summary>
    private EditResult Change(Func<string?> apply, Func<string> done)
    {
        var before = Capture();
        string? refusal;
        try
        {
            refusal = apply();
        }
        catch
        {
            Restore(before);
            throw;
        }

        if (refusal is not null)
        {
            Restore(before);
            return EditResult.Refused(refusal);
        }

        _undo.Add(before);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        ChangeCount++;
        return EditResult.Ok(done());
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private bool CursorIsValid() => IsValid(Cursor);

    private bool IsValid(EditPosition at) =>
        at.Part >= 0 && at.Part < Project.Parts.Count
        && at.Staff >= 0 && at.Staff < Project.Parts[at.Part].Staves.Count
        && at.Measure >= 0 && at.Measure < Project.Parts[at.Part].Measures.Count
        && at.Voice >= 1 && at.Position.Sign >= 0 && at.Position < MeasureLength(Project.Parts[at.Part].Measures[at.Measure]);

    private ScoreMeasure MeasureAt(EditPosition at) => Project.Parts[at.Part].Measures[at.Measure];

    public static Rational MeasureLength(ScoreMeasure measure) => new(4L * measure.Numerator, Math.Max(1, measure.Denominator));

    private static Rational Start(ScoreEvent e) => new(e.PositionNumerator, e.PositionDenominator);

    private static Rational Duration(ScoreEvent e) => new(e.DurationNumerator, e.DurationDenominator);

    private static void SetStart(ScoreEvent e, Rational start) =>
        (e.PositionNumerator, e.PositionDenominator) = ((int)start.Numerator, (int)start.Denominator);

    private static void SetDuration(ScoreEvent e, Rational length) =>
        (e.DurationNumerator, e.DurationDenominator) = ((int)length.Numerator, (int)length.Denominator);

    private static ScoreEvent WithTime(ScoreEvent e, Rational start, Rational length)
    {
        SetStart(e, start);
        SetDuration(e, length);
        return e;
    }

    /// <summary>The chords (notes and rests grouped by start) of one voice in a measure, in time order.</summary>
    private static List<List<ScoreEvent>> VoiceChords(ScoreMeasure measure, int staff, int voice) =>
        measure.Events.Where(e => e.Staff == staff && e.Voice == voice)
            .GroupBy(Start).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();

    private List<ScoreEvent>? ChordAt(EditPosition at)
    {
        if (!IsValid(at with { Position = Rational.Zero })) return null;
        var chord = MeasureAt(at).Events.Where(e => e.Staff == at.Staff && e.Voice == at.Voice && Start(e) == at.Position).ToList();
        return chord.Count > 0 ? chord : null;
    }

    private static Rational Length(NoteType type, int dots)
    {
        var length = Notation.Length(type);
        var add = length;
        for (var d = 0; d < dots; d++)
        {
            add = new Rational(add.Numerator, add.Denominator * 2);
            length += add;
        }

        return length;
    }

    /// <summary>Splits a length into written values (longest first, at most one dot), for rests and cut notes.</summary>
    private static List<Rational> Pieces(Rational length)
    {
        var values = Enumerable.Range((int)NoteType.Whole, (int)NoteType.SixtyFourth - (int)NoteType.Whole + 1)
            .SelectMany(t => new[] { Length((NoteType)t, 1), Length((NoteType)t, 0) })
            .ToList();
        var pieces = new List<Rational>();
        while (length.Sign > 0)
        {
            var piece = values.FirstOrDefault(v => v <= length);
            if (piece.Sign <= 0) piece = length; // shorter than a 64th: keep it exact
            pieces.Add(piece);
            length -= piece;
        }

        return pieces;
    }

    /// <summary>Deep copies of events, through the project file format so every property comes along.</summary>
    private static List<ScoreEvent> Clone(IEnumerable<ScoreEvent> events) =>
        ProjectFileService.FromJson(ProjectFileService.ToJson(new ScoreProject
        {
            Parts = { new ScorePart { Measures = { new ScoreMeasure { Events = events.ToList() } } } },
        })).Parts[0].Measures[0].Events;

    private static ScoreEvent NewRest(EditPosition at, Rational start, Rational length) =>
        WithTime(new ScoreEvent { Kind = "Rest", Staff = at.Staff, Voice = at.Voice }, start, length);

    private static void AddRests(ScoreMeasure measure, EditPosition at, Rational from, Rational to)
    {
        foreach (var piece in Pieces(to - from))
        {
            measure.Events.Add(NewRest(at, from, piece));
            from += piece;
        }
    }

    /// <summary>
    /// Frees the time from <paramref name="from"/> to <paramref name="to"/> in one voice: chords inside it go; a chord that
    /// starts before keeps its part before (notes tied over the pieces); what lasts beyond becomes rests.
    /// </summary>
    private static void ClearSpan(ScoreMeasure measure, int staff, int voice, Rational from, Rational to)
    {
        var at = new EditPosition(0, staff, voice, 0, from);
        foreach (var chord in VoiceChords(measure, staff, voice))
        {
            var start = Start(chord[0]);
            var end = start + Duration(chord[0]);
            if (end <= from || start >= to) continue;

            foreach (var e in chord) measure.Events.Remove(e);
            if (start < from)
            {
                var pieces = Pieces(from - start);
                var pieceStart = start;
                for (var i = 0; i < pieces.Count; i++)
                {
                    foreach (var copy in Clone(chord))
                    {
                        copy.TieToNext = copy.Kind == "Note" && i < pieces.Count - 1;
                        if (i > 0) copy.Lyrics = new List<ScoreLyric>();
                        measure.Events.Add(WithTime(copy, pieceStart, pieces[i]));
                    }

                    pieceStart += pieces[i];
                }
            }

            if (end > to) AddRests(measure, at, to, end);
        }
    }

    private static Rational Min(Rational a, Rational b) => a < b ? a : b;

    /// <summary>
    /// The chord's notes with every note tied to them: back through notes tying into them and on through their own ties,
    /// across measures of the voice.
    /// </summary>
    private List<ScoreEvent> TiedWith(EditPosition at, List<ScoreEvent> chord)
    {
        var part = Project.Parts[at.Part];
        var chords = Enumerable.Range(0, part.Measures.Count)
            .SelectMany(m => VoiceChords(part.Measures[m], at.Staff, at.Voice).Select(c => (Measure: m, Events: c)))
            .ToList();
        var index = chords.FindIndex(c => c.Measure == at.Measure && Start(c.Events[0]) == at.Position);
        var result = new List<ScoreEvent>(chord);
        foreach (var note in chord)
        {
            var pitch = note.MidiPitch;
            for (var i = index - 1; i >= 0 && chords[i].Events.FirstOrDefault(e => e.MidiPitch == pitch && e.TieToNext) is { } before; i--)
                result.Add(before);
            var current = note;
            for (var i = index + 1; i < chords.Count && current.TieToNext && chords[i].Events.FirstOrDefault(e => e.MidiPitch == pitch) is { } after; i++)
            {
                result.Add(after);
                current = after;
            }
        }

        return result.Distinct().ToList();
    }

    /// <summary>Notes of the chord just before <paramref name="at"/> in its voice stop tying into these pitches.</summary>
    private void UntieInto(EditPosition at, List<ScoreEvent> chord)
    {
        var measure = MeasureAt(at);
        var before = VoiceChords(measure, at.Staff, at.Voice).LastOrDefault(c => Start(c[0]) < at.Position);
        if (before is null && at.Measure > 0)
            before = VoiceChords(Project.Parts[at.Part].Measures[at.Measure - 1], at.Staff, at.Voice).LastOrDefault();
        foreach (var e in before ?? new List<ScoreEvent>())
        {
            if (chord.Any(c => c.MidiPitch == e.MidiPitch)) e.TieToNext = false;
        }
    }

    /// <summary>
    /// Diatonic number to choose octaves near: the previous note of the voice (searching back through measures), or the
    /// middle line of the staff's clef.
    /// </summary>
    private int ReferenceDiatonic(EditPosition at)
    {
        var part = Project.Parts[at.Part];
        for (var m = at.Measure; m >= 0; m--)
        {
            var measure = part.Measures[m];
            var previous = VoiceChords(measure, at.Staff, at.Voice)
                .LastOrDefault(c => c[0].Kind == "Note" && (m < at.Measure || Start(c[0]) < at.Position));
            if (previous is not null)
                return Notation.Spell(previous.OrderBy(e => e.MidiPitch).Last(), measure.KeyFifths).Diatonic;
        }

        var clef = new ClefTimeline(Project).ClefAt(at.Part, at.Staff, at.Measure, at.Position);
        return Notation.BottomLineDiatonic(clef) + 4;
    }

    /// <summary>A note of <paramref name="step"/> in the key, in the octave nearest to <paramref name="reference"/>.</summary>
    private static ScoreEvent? MakeNote(EditPosition at, int step, int reference, int keyFifths) =>
        NoteAt(at, (int)Math.Round((reference - step) / 7.0, MidpointRounding.AwayFromZero), step, keyFifths);

    private static ScoreEvent? NoteAt(EditPosition at, int octave, int step, int keyFifths)
    {
        var alter = Notation.KeyAlter(keyFifths, step);
        var midi = 12 * (octave + 1) + StepSemitones[step] + alter;
        if (midi is < 0 or > 127) return null;
        var accidental = alter > 0 ? new string('#', alter) : new string('b', -alter);
        return new ScoreEvent
        {
            Kind = "Note", MidiPitch = midi, Staff = at.Staff, Voice = at.Voice,
            NotationSpelling = $"{StepLetters[step]}{accidental}{octave.ToString(CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>Adds an empty measure after <paramref name="index"/> in every part, with the time and key of that measure.</summary>
    private void AddMeasureAfter(int index)
    {
        foreach (var part in Project.Parts.Where(p => index < p.Measures.Count))
        {
            var source = part.Measures[index];
            part.Measures.Insert(index + 1, new ScoreMeasure
            {
                Number = source.Number + 1, Numerator = source.Numerator, Denominator = source.Denominator, KeyFifths = source.KeyFifths,
            });
            for (var m = index + 2; m < part.Measures.Count; m++) part.Measures[m].Number++;
        }
    }

    // ---- Beams ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Beams the 8th notes and shorter of one voice in a measure by beat (a quarter note; a dotted quarter in 6/8, 9/8 and
    /// 12/8), breaking at rests. Secondary beams join neighbouring 16ths and shorter; a lone one gets a hook.
    /// </summary>
    public static void AutoBeam(ScoreMeasure measure, int staff, int voice)
    {
        var unit = BeatUnit(measure);
        var chords = VoiceChords(measure, staff, voice);
        foreach (var e in chords.SelectMany(c => c)) e.Beams = new List<BeamType>();

        var group = new List<(List<ScoreEvent> Chord, int Levels)>();
        Rational groupEnd = default;
        long groupBeat = -1;
        foreach (var chord in chords)
        {
            var start = Start(chord[0]);
            var length = Duration(chord[0]);
            var value = Notation.ValueOf(length);
            var beat = (long)Math.Floor(start.ToDouble() / unit.ToDouble() + 1e-9);
            var lastBeat = (long)Math.Floor((start + length).ToDouble() / unit.ToDouble() - 1e-9);
            var beamable = chord[0].Kind == "Note" && value.IsExact && !value.IsTuplet && value.Type >= NoteType.Eighth && beat == lastBeat;

            if (group.Count > 0 && (!beamable || beat != groupBeat || start != groupEnd))
            {
                SetBeams(group);
                group.Clear();
            }

            if (!beamable) continue;
            group.Add((chord, value.Type - NoteType.Eighth + 1));
            groupBeat = beat;
            groupEnd = start + length;
        }

        SetBeams(group);
    }

    private static void SetBeams(List<(List<ScoreEvent> Chord, int Levels)> group)
    {
        if (group.Count < 2) return;
        var beams = group.Select(_ => new List<BeamType>()).ToList();
        for (var level = 0; level < group.Max(g => g.Levels); level++)
        {
            for (var i = 0; i < group.Count; i++)
            {
                if (group[i].Levels <= level) continue;
                var hasBefore = i > 0 && group[i - 1].Levels > level;
                var hasAfter = i + 1 < group.Count && group[i + 1].Levels > level;
                beams[i].Add((hasBefore, hasAfter) switch
                {
                    (false, true) => BeamType.Begin,
                    (true, true) => BeamType.Continue,
                    (true, false) => BeamType.End,
                    _ => i == 0 ? BeamType.ForwardHook : BeamType.BackwardHook,
                });
            }
        }

        for (var i = 0; i < group.Count; i++)
        {
            foreach (var e in group[i].Chord) e.Beams = new List<BeamType>(beams[i]);
        }
    }

    // ---- Descriptions --------------------------------------------------------------------------------------------

    /// <summary>A note value in Korean, e.g. "점4분음표" or "8분쉼표".</summary>
    public static string DescribeValue(NoteType type, int dots, bool rest)
    {
        var name = type switch
        {
            NoteType.Breve => "겹온",
            NoteType.Whole => "온",
            NoteType.Half => "2분",
            NoteType.Quarter => "4분",
            NoteType.Eighth => "8분",
            NoteType.Sixteenth => "16분",
            NoteType.ThirtySecond => "32분",
            _ => "64분",
        };
        return $"{(dots == 2 ? "겹점" : dots == 1 ? "점" : "")}{name}{(rest ? "쉼표" : "음표")}";
    }

    private static string DescribeLength(Rational length) =>
        Notation.ValueOf(length) is { IsExact: true, IsTuplet: false } value
            ? DescribeValue(value.Type, value.Dots, rest: false).Replace("음표", "")
            : $"4분음표 {length}개";

    private static string DescribeBeat(Rational position) => $"{(position.ToDouble() + 1).ToString("0.##", CultureInfo.InvariantCulture)}박";
}
