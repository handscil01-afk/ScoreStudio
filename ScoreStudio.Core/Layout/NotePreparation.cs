using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Layout;

/// <summary>One notehead of a chord, ready to draw.</summary>
/// <param name="Position">Staff position in half spaces: 0 is the bottom line, 8 the top line.</param>
/// <param name="Accidental">Alteration to print in front of the note (0 prints a natural), or null for none.</param>
/// <param name="Side">−1 when the head sits left of the stem side, 1 when right of it (seconds in chords), else 0.</param>
/// <param name="AccidentalColumn">0 for the column next to the noteheads, 1 for the next one to the left, …</param>
internal sealed record PreparedNote(int Position, int? Accidental, int Side, int AccidentalColumn);

/// <summary>Notes of one voice that start together on one staff (a chord or a single note), or a rest.</summary>
internal sealed class PreparedChord
{
    public required int PartIndex { get; init; }
    public required int Staff { get; init; }
    public required int Voice { get; init; }
    public required Rational Start { get; init; }
    public required NoteValue Value { get; init; }
    public required bool IsRest { get; init; }

    /// <summary>A rest that fills the whole measure; drawn as a whole rest in the middle of the measure.</summary>
    public bool IsMeasureRest { get; init; }

    public bool StemUp { get; init; }

    /// <summary>Set when the staff has more than one voice in this measure: the first voice goes up, the others down.</summary>
    public int VoiceRole { get; init; }

    public IReadOnlyList<PreparedNote> Notes { get; init; } = Array.Empty<PreparedNote>();

    /// <summary>Octave line the chord is under: 1 = 8va, 2 = 15ma, −1 = 8vb, −2 = 15mb.</summary>
    public int OctaveShift { get; init; }

    /// <summary>The chord's beams per level (see <see cref="ScoreEvent.Beams"/>); empty when it is drawn with a flag.</summary>
    public IReadOnlyList<BeamType> Beams { get; init; } = Array.Empty<BeamType>();

    public bool IsBeamed => Beams.Count > 0;

    /// <summary>Room needed left of the onset for accidentals and left-displaced heads, in staff spaces.</summary>
    public double LeftExtent { get; init; }

    /// <summary>Room needed right of the onset before the next onset can start, in staff spaces.</summary>
    public double RightExtent { get; init; }
}

/// <summary>Chords of one voice joined by beams, all with stems in the same direction.</summary>
internal sealed record PreparedBeam(IReadOnlyList<PreparedChord> Chords, bool StemUp);

/// <summary>A clef change in the middle of a measure, drawn just before the notes at <see cref="Onset"/>.</summary>
internal sealed record MidMeasureClef(int PartIndex, int Staff, Rational Onset, ClefType Clef);

/// <summary>Everything about one measure (across all parts) that does not depend on where the measure lands.</summary>
internal sealed class PreparedMeasure
{
    public required List<PreparedChord> Chords { get; init; }

    public IReadOnlyList<PreparedBeam> Beams { get; init; } = Array.Empty<PreparedBeam>();

    public IReadOnlyList<MidMeasureClef> MidMeasureClefs { get; init; } = Array.Empty<MidMeasureClef>();
    public required List<Rational> Onsets { get; init; }

    /// <summary>Room before each onset for accidentals, in staff spaces.</summary>
    public required double[] Pre { get; init; }

    /// <summary>Space from each onset to the next (or to the barline), in staff spaces.</summary>
    public required double[] Segments { get; init; }

    public bool IsEmpty => Onsets.Count == 0;
}

/// <summary>
/// Turns stored events into chords with display decisions: written note values, staff positions, which accidentals
/// to print (key signature and earlier notes in the measure), stem directions and spacing needs.
/// </summary>
internal static class NotePreparation
{
    public const double AccidentalColumnWidth = 1.1;
    public const double AccidentalGap = 0.25;

    /// <summary>Room for a clef change before a note in mid-measure, in staff spaces.</summary>
    public const double ClefChangeWidth = 2.6;

    public static PreparedMeasure[] Prepare(ScoreProject project, int measureCount, ClefTimeline clefs)
    {
        var measures = new PreparedMeasure[measureCount];
        var tiedFromPrevious = new HashSet<(int Part, int Staff, int Voice, int Midi)>();
        var chordsByMeasure = Enumerable.Range(0, measureCount).Select(_ => new List<PreparedChord>()).ToArray();
        var beamsByMeasure = Enumerable.Range(0, measureCount).Select(_ => new List<PreparedBeam>()).ToArray();
        var midClefs = Enumerable.Range(0, measureCount).Select(_ => new List<MidMeasureClef>()).ToArray();
        var lengths = new Rational[measureCount];

        for (var p = 0; p < project.Parts.Count; p++)
        {
            var part = project.Parts[p];
            for (var m = 0; m < part.Measures.Count && m < measureCount; m++)
            {
                var measure = part.Measures[m];
                var nominal = new Rational(4L * measure.Numerator, measure.Denominator);
                lengths[m] = Rational.Max(lengths[m] == default ? Rational.Zero : lengths[m], nominal);
                foreach (var e in measure.Events)
                    lengths[m] = Rational.Max(lengths[m], Start(e) + Duration(e));

                PrepareMeasurePart(part, p, m, measure, nominal, tiedFromPrevious, clefs, chordsByMeasure[m], beamsByMeasure[m]);
                foreach (var change in measure.ClefChanges.Where(c => c.PositionNumerator > 0))
                    midClefs[m].Add(new MidMeasureClef(p, change.Staff, new Rational(change.PositionNumerator, change.PositionDenominator), change.Clef));
            }
        }

        for (var m = 0; m < measureCount; m++)
            measures[m] = Space(chordsByMeasure[m], beamsByMeasure[m], midClefs[m], lengths[m] == default ? new Rational(4, 1) : lengths[m]);
        return measures;
    }

    private static Rational Start(ScoreEvent e) => new(e.PositionNumerator, e.PositionDenominator);
    private static Rational Duration(ScoreEvent e) => new(e.DurationNumerator, e.DurationDenominator);

    /// <summary>A chord whose notes are known but whose stem direction may still depend on its beam group.</summary>
    private sealed class PendingChord
    {
        public required ScoreEvent First { get; init; }
        public required NoteValue Value { get; init; }
        public required int Role { get; init; }
        public required List<(int Position, int? Accidental)> Notes { get; init; }
        public required IReadOnlyList<BeamType> Beams { get; set; }
        public bool? GroupStemUp { get; set; }
    }

    private static void PrepareMeasurePart(
        ScorePart part, int partIndex, int measureIndex, ScoreMeasure measure, Rational nominal,
        HashSet<(int Part, int Staff, int Voice, int Midi)> tiedFromPrevious, ClefTimeline clefs,
        List<PreparedChord> result, List<PreparedBeam> beams)
    {
        var events = measure.Events.Select((e, i) => (Event: e, Order: i)).OrderBy(x => Start(x.Event)).ThenBy(x => x.Order).Select(x => x.Event);

        foreach (var staffGroup in events.GroupBy(e => e.Staff))
        {
            var staff = staffGroup.Key;
            var voices = staffGroup.Select(e => e.Voice).Distinct().OrderBy(v => v).ToList();
            var multiVoice = voices.Count > 1;

            // Alteration currently in force at each diatonic step, starting from the key signature.
            var inForce = new Dictionary<int, int>();
            var pending = new List<PendingChord>();

            foreach (var chordEvents in staffGroup.GroupBy(e => (e.Voice, Start: Start(e))).OrderBy(g => g.Key.Start))
            {
                var first = chordEvents.First();
                var duration = Duration(first);
                var clef = clefs.ClefAt(partIndex, staff, measureIndex, Start(first));
                var value = Notation.ValueOf(duration);
                var role = multiVoice ? (first.Voice == voices[0] ? 1 : -1) : 0;

                if (first.Kind == "Rest")
                {
                    var isMeasureRest = Start(first).Sign == 0 && duration >= nominal;
                    result.Add(new PreparedChord
                    {
                        PartIndex = partIndex, Staff = staff, Voice = first.Voice, Start = Start(first), Value = value,
                        IsRest = true, IsMeasureRest = isMeasureRest, VoiceRole = role, OctaveShift = first.OctaveShift,
                        RightExtent = isMeasureRest ? 0 : Smufl.RestWidth(value.Type) + DotsWidth(value),
                    });
                    continue;
                }

                var notes = new List<(int Position, int? Accidental)>();
                foreach (var note in chordEvents.Where(e => e.Kind == "Note").OrderBy(e => e.MidiPitch))
                {
                    // Under an octave line the note is written one or two octaves away from its sounding pitch.
                    var pitch = Notation.Spell(note, measure.KeyFifths);
                    pitch = pitch with { Octave = pitch.Octave - note.OctaveShift };
                    var position = Notation.StaffPosition(pitch, clef);
                    var current = inForce.TryGetValue(pitch.Diatonic, out var known) ? known : Notation.KeyAlter(measure.KeyFifths, pitch.Step);
                    var tieKey = (partIndex, staff, note.Voice, note.MidiPitch);

                    int? accidental = null;
                    if (tiedFromPrevious.Contains(tieKey))
                    {
                        // A note tied over from before shows no accidental; later notes in the measure still need one.
                    }
                    else if (pitch.Alter != current)
                    {
                        accidental = pitch.Alter;
                        inForce[pitch.Diatonic] = pitch.Alter;
                    }

                    if (note.TieToNext) tiedFromPrevious.Add(tieKey);
                    else tiedFromPrevious.Remove(tieKey);
                    notes.Add((position, accidental));
                }

                if (notes.Count == 0)
                    continue;

                pending.Add(new PendingChord
                {
                    First = first, Value = value, Role = role, Notes = notes,
                    Beams = chordEvents.Select(e => e.Beams).FirstOrDefault(b => b is { Count: > 0 }) ?? new List<BeamType>(),
                });
            }

            var groups = BeamGroups(pending);
            var built = new Dictionary<PendingChord, PreparedChord>();
            foreach (var chord in pending)
            {
                var stemUp = chord.GroupStemUp ?? StemUp(chord.Role, chord.Notes.Select(n => n.Position).ToList());
                built[chord] = BuildChord(partIndex, staff, chord.First.Voice, Start(chord.First), chord.Value, stemUp, chord.Role,
                    chord.Notes, chord.First.OctaveShift, chord.Beams);
                result.Add(built[chord]);
            }

            beams.AddRange(groups.Select(g => new PreparedBeam(g.Select(c => built[c]).ToList(), g[0].GroupStemUp!.Value)));
        }
    }

    /// <summary>
    /// One voice keeps one stem direction; otherwise the note furthest from the middle line decides (stems go down from it).
    /// </summary>
    private static bool StemUp(int role, IReadOnlyCollection<int> positions) =>
        role != 0 ? role > 0 : (4 - positions.Min()) > (positions.Max() - 4);

    /// <summary>
    /// Joins chords into beam groups by their level-1 beams: a group runs from "begin" to "end" within one voice.
    /// Broken groups (no end, a gap, a note too long for a beam) fall back to flags rather than guessing.
    /// Sets each grouped chord's shared stem direction.
    /// </summary>
    private static List<List<PendingChord>> BeamGroups(List<PendingChord> chords)
    {
        var groups = new List<List<PendingChord>>();
        foreach (var voice in chords.GroupBy(c => c.First.Voice))
        {
            List<PendingChord>? open = null;
            foreach (var chord in voice.OrderBy(c => Start(c.First)))
            {
                BeamType? primary = chord.Beams.Count > 0 ? chord.Beams[0] : null;
                if (primary == BeamType.Begin)
                {
                    Unbeam(open);
                    open = new List<PendingChord> { chord };
                }
                else if (primary is BeamType.Continue or BeamType.End && open is not null)
                {
                    open.Add(chord);
                    if (primary == BeamType.End)
                    {
                        groups.Add(open);
                        open = null;
                    }
                }
                else
                {
                    Unbeam(open);
                    open = null;
                    chord.Beams = Array.Empty<BeamType>();
                }
            }

            Unbeam(open);
        }

        groups.RemoveAll(g =>
        {
            if (g.Count >= 2 && g.All(c => c.Value.Type >= NoteType.Eighth)) return false;
            Unbeam(g);
            return true;
        });

        foreach (var group in groups)
        {
            var stemUp = StemUp(group[0].Role, group.SelectMany(c => c.Notes.Select(n => n.Position)).ToList());
            foreach (var chord in group) chord.GroupStemUp = stemUp;
        }

        return groups;
    }

    private static void Unbeam(List<PendingChord>? group)
    {
        if (group is null) return;
        foreach (var chord in group) chord.Beams = Array.Empty<BeamType>();
    }

    private static double DotsWidth(NoteValue value) => value.Dots == 0 ? 0 : 0.35 + value.Dots * 0.6;

    private static PreparedChord BuildChord(
        int partIndex, int staff, int voice, Rational start, NoteValue value, bool stemUp, int role,
        List<(int Position, int? Accidental)> notes, int octaveShift, IReadOnlyList<BeamType> beams)
    {
        var sorted = notes.OrderBy(n => n.Position).ToList();
        var sides = new int[sorted.Count];

        // Seconds cannot share a column: the second head of each pair moves to the other side of the stem.
        // Stem up: walk upwards and push heads right; stem down: walk downwards and push heads left.
        if (stemUp)
        {
            for (var i = 1; i < sorted.Count; i++)
                if (sorted[i].Position - sorted[i - 1].Position == 1 && sides[i - 1] == 0) sides[i] = 1;
        }
        else
        {
            for (var i = sorted.Count - 2; i >= 0; i--)
                if (sorted[i + 1].Position - sorted[i].Position == 1 && sides[i + 1] == 0) sides[i] = -1;
        }

        // Accidentals from the top down: a new column when the previous accidental in a column is less than a sixth away.
        var columns = new int[sorted.Count];
        var lastInColumn = new List<int>();
        for (var i = sorted.Count - 1; i >= 0; i--)
        {
            if (sorted[i].Accidental is null) continue;
            var column = 0;
            while (column < lastInColumn.Count && lastInColumn[column] - sorted[i].Position < 6) column++;
            if (column == lastInColumn.Count) lastInColumn.Add(sorted[i].Position);
            else lastInColumn[column] = sorted[i].Position;
            columns[i] = column;
        }

        var headWidth = Smufl.NoteheadWidth(value.Type);
        var displacedLeft = sides.Any(s => s < 0) ? headWidth : 0;
        var displacedRight = sides.Any(s => s > 0) ? headWidth : 0;
        var accidentalRoom = lastInColumn.Count == 0 ? 0 : lastInColumn.Count * AccidentalColumnWidth + AccidentalGap;
        var flagRoom = stemUp && value.Type >= NoteType.Eighth && beams.Count == 0 ? 1.1 : 0;

        return new PreparedChord
        {
            PartIndex = partIndex, Staff = staff, Voice = voice, Start = start, Value = value, IsRest = false,
            StemUp = stemUp, VoiceRole = role, OctaveShift = octaveShift, Beams = beams,
            Notes = sorted.Select((n, i) => new PreparedNote(n.Position, n.Accidental, sides[i], columns[i])).ToList(),
            LeftExtent = displacedLeft + accidentalRoom,
            RightExtent = headWidth + displacedRight + Math.Max(DotsWidth(value), flagRoom),
        };
    }

    /// <summary>
    /// Horizontal spacing: every onset in the measure (across all parts) gets a column, and the gap to the next onset
    /// grows with the time between them, so simultaneous notes line up in every part.
    /// </summary>
    private static PreparedMeasure Space(List<PreparedChord> chords, List<PreparedBeam> beams, List<MidMeasureClef> midClefs, Rational length)
    {
        var onsets = chords.Where(c => !c.IsMeasureRest).Select(c => c.Start).Distinct().OrderBy(t => t).ToList();
        if (onsets.Count == 0 && chords.Count > 0)
            onsets.Add(Rational.Zero); // only whole-measure rests: one column

        // A mid-measure clef is drawn before the first onset at or after its position.
        var clefsAtOnsets = midClefs
            .Select(c => c with { Onset = onsets.FirstOrDefault(t => t >= c.Onset, c.Onset) })
            .Where(c => onsets.Contains(c.Onset))
            .ToList();

        var pre = new double[onsets.Count];
        var segments = new double[onsets.Count];
        for (var i = 0; i < onsets.Count; i++)
        {
            var atOnset = chords.Where(c => c.Start == onsets[i] && !c.IsMeasureRest).ToList();
            pre[i] = atOnset.Select(c => c.LeftExtent).DefaultIfEmpty(0).Max();
            if (clefsAtOnsets.Any(c => c.Onset == onsets[i]))
                pre[i] += ClefChangeWidth;
            var next = i + 1 < onsets.Count ? onsets[i + 1] : length;
            var gap = SpaceFor(next - onsets[i]);
            var needed = atOnset.Select(c => c.RightExtent + 0.3).DefaultIfEmpty(0).Max();
            segments[i] = Math.Max(gap, needed);
        }

        return new PreparedMeasure { Chords = chords, Beams = beams, MidMeasureClefs = clefsAtOnsets, Onsets = onsets, Pre = pre, Segments = segments };
    }

    /// <summary>
    /// Space after a note, in staff spaces: about 2.5 for a 16th, 3.2 for an 8th, 4.1 for a quarter, 5.3 for a half and
    /// 6.7 for a whole, close to common engraving practice (and to Finale's default spacing).
    /// </summary>
    public static double SpaceFor(Rational duration) => 1.6 + 1.6 * Math.Log2(1 + 2 * Math.Max(duration.ToDouble(), 0));
}

/// <summary>Which clef is in force on each staff at any point of the piece.</summary>
internal sealed class ClefTimeline
{
    private readonly Dictionary<(int Part, int Staff), List<(int Measure, Rational Position, ClefType Clef)>> _changes = new();
    private readonly Dictionary<(int Part, int Staff), ClefType> _initial = new();

    public ClefTimeline(ScoreProject project)
    {
        for (var p = 0; p < project.Parts.Count; p++)
        {
            var part = project.Parts[p];
            for (var s = 0; s < part.Staves.Count; s++)
                _initial[(p, s)] = part.Staves[s].Clef;

            for (var m = 0; m < part.Measures.Count; m++)
            {
                foreach (var change in part.Measures[m].ClefChanges)
                {
                    var key = (p, change.Staff);
                    if (!_changes.TryGetValue(key, out var list)) _changes[key] = list = new();
                    list.Add((m, new Rational(change.PositionNumerator, change.PositionDenominator), change.Clef));
                }
            }
        }

        foreach (var list in _changes.Values)
            list.Sort((a, b) => a.Measure != b.Measure ? a.Measure.CompareTo(b.Measure) : a.Position.CompareTo(b.Position));
    }

    /// <summary>The clef in force at a point; a change exactly at that point already counts.</summary>
    public ClefType ClefAt(int part, int staff, int measure, Rational position)
    {
        var clef = _initial.TryGetValue((part, staff), out var initial) ? initial : ClefType.Treble;
        if (_changes.TryGetValue((part, staff), out var list))
        {
            foreach (var change in list)
            {
                if (change.Measure > measure || (change.Measure == measure && change.Position > position)) break;
                clef = change.Clef;
            }
        }

        return clef;
    }

    /// <summary>The clef a staff changes to at the very start of a measure, or null.</summary>
    public ClefType? ChangeAtMeasureStart(int part, int staff, int measure)
    {
        if (!_changes.TryGetValue((part, staff), out var list)) return null;

        ClefType? result = null;
        foreach (var change in list)
        {
            if (change.Measure == measure && change.Position.Sign == 0) result = change.Clef;
        }

        return result;
    }
}
