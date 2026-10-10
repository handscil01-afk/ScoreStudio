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

    /// <summary>Room needed left of the onset for accidentals and left-displaced heads, in staff spaces.</summary>
    public double LeftExtent { get; init; }

    /// <summary>Room needed right of the onset before the next onset can start, in staff spaces.</summary>
    public double RightExtent { get; init; }
}

/// <summary>Everything about one measure (across all parts) that does not depend on where the measure lands.</summary>
internal sealed class PreparedMeasure
{
    public required List<PreparedChord> Chords { get; init; }
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

    public static PreparedMeasure[] Prepare(ScoreProject project, int measureCount)
    {
        var measures = new PreparedMeasure[measureCount];
        var tiedFromPrevious = new HashSet<(int Part, int Staff, int Voice, int Midi)>();
        var chordsByMeasure = Enumerable.Range(0, measureCount).Select(_ => new List<PreparedChord>()).ToArray();
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

                chordsByMeasure[m].AddRange(PrepareMeasurePart(part, p, measure, nominal, tiedFromPrevious));
            }
        }

        for (var m = 0; m < measureCount; m++)
            measures[m] = Space(chordsByMeasure[m], lengths[m] == default ? new Rational(4, 1) : lengths[m]);
        return measures;
    }

    private static Rational Start(ScoreEvent e) => new(e.PositionNumerator, e.PositionDenominator);
    private static Rational Duration(ScoreEvent e) => new(e.DurationNumerator, e.DurationDenominator);

    private static IEnumerable<PreparedChord> PrepareMeasurePart(
        ScorePart part, int partIndex, ScoreMeasure measure, Rational nominal,
        HashSet<(int Part, int Staff, int Voice, int Midi)> tiedFromPrevious)
    {
        var result = new List<PreparedChord>();
        var events = measure.Events.Select((e, i) => (Event: e, Order: i)).OrderBy(x => Start(x.Event)).ThenBy(x => x.Order).Select(x => x.Event);

        foreach (var staffGroup in events.GroupBy(e => e.Staff))
        {
            var staff = staffGroup.Key;
            var clef = part.Staves[Math.Clamp(staff, 0, part.Staves.Count - 1)].Clef;
            var voices = staffGroup.Select(e => e.Voice).Distinct().OrderBy(v => v).ToList();
            var multiVoice = voices.Count > 1;

            // Alteration currently in force at each diatonic step, starting from the key signature.
            var inForce = new Dictionary<int, int>();

            foreach (var chordEvents in staffGroup.GroupBy(e => (e.Voice, Start: Start(e))).OrderBy(g => g.Key.Start))
            {
                var first = chordEvents.First();
                var duration = Duration(first);
                var value = Notation.ValueOf(duration);
                var role = multiVoice ? (first.Voice == voices[0] ? 1 : -1) : 0;

                if (first.Kind == "Rest")
                {
                    var isMeasureRest = Start(first).Sign == 0 && duration >= nominal;
                    result.Add(new PreparedChord
                    {
                        PartIndex = partIndex, Staff = staff, Voice = first.Voice, Start = Start(first), Value = value,
                        IsRest = true, IsMeasureRest = isMeasureRest, VoiceRole = role,
                        RightExtent = isMeasureRest ? 0 : Smufl.RestWidth(value.Type) + DotsWidth(value),
                    });
                    continue;
                }

                var notes = new List<(int Position, int? Accidental)>();
                foreach (var note in chordEvents.Where(e => e.Kind == "Note").OrderBy(e => e.MidiPitch))
                {
                    var pitch = Notation.Spell(note, measure.KeyFifths);
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

                var low = notes.Min(n => n.Position);
                var high = notes.Max(n => n.Position);
                var stemUp = role != 0 ? role > 0 : (4 - low) > (high - 4);
                result.Add(BuildChord(partIndex, staff, first.Voice, Start(first), value, stemUp, role, notes));
            }
        }

        return result;
    }

    private static double DotsWidth(NoteValue value) => value.Dots == 0 ? 0 : 0.35 + value.Dots * 0.6;

    private static PreparedChord BuildChord(
        int partIndex, int staff, int voice, Rational start, NoteValue value, bool stemUp, int role,
        List<(int Position, int? Accidental)> notes)
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
        var flagRoom = stemUp && value.Type >= NoteType.Eighth ? 1.1 : 0;

        return new PreparedChord
        {
            PartIndex = partIndex, Staff = staff, Voice = voice, Start = start, Value = value, IsRest = false,
            StemUp = stemUp, VoiceRole = role,
            Notes = sorted.Select((n, i) => new PreparedNote(n.Position, n.Accidental, sides[i], columns[i])).ToList(),
            LeftExtent = displacedLeft + accidentalRoom,
            RightExtent = headWidth + displacedRight + Math.Max(DotsWidth(value), flagRoom),
        };
    }

    /// <summary>
    /// Horizontal spacing: every onset in the measure (across all parts) gets a column, and the gap to the next onset
    /// grows with the time between them, so simultaneous notes line up in every part.
    /// </summary>
    private static PreparedMeasure Space(List<PreparedChord> chords, Rational length)
    {
        var onsets = chords.Where(c => !c.IsMeasureRest).Select(c => c.Start).Distinct().OrderBy(t => t).ToList();
        if (onsets.Count == 0 && chords.Count > 0)
            onsets.Add(Rational.Zero); // only whole-measure rests: one column

        var pre = new double[onsets.Count];
        var segments = new double[onsets.Count];
        for (var i = 0; i < onsets.Count; i++)
        {
            var atOnset = chords.Where(c => c.Start == onsets[i] && !c.IsMeasureRest).ToList();
            pre[i] = atOnset.Select(c => c.LeftExtent).DefaultIfEmpty(0).Max();
            var next = i + 1 < onsets.Count ? onsets[i + 1] : length;
            var gap = SpaceFor(next - onsets[i]);
            var needed = atOnset.Select(c => c.RightExtent + 0.5).DefaultIfEmpty(0).Max();
            segments[i] = Math.Max(gap, needed);
        }

        return new PreparedMeasure { Chords = chords, Onsets = onsets, Pre = pre, Segments = segments };
    }

    /// <summary>Space after a note, in staff spaces: about 4 for an 8th, 5 for a quarter, 6.4 for a half, 7.9 for a whole.</summary>
    public static double SpaceFor(Rational duration) => 2.2 + 1.8 * Math.Log2(1 + 2 * Math.Max(duration.ToDouble(), 0));
}
