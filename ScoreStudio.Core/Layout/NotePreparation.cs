using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Layout;

/// <summary>One notehead of a chord, ready to draw.</summary>
/// <param name="Position">Staff position in half spaces: 0 is the bottom line, 8 the top line.</param>
/// <param name="Accidental">Alteration to print in front of the note (0 prints a natural), or null for none.</param>
/// <param name="Side">−1 when the head sits left of the stem side, 1 when right of it (seconds in chords), else 0.</param>
/// <param name="AccidentalColumn">0 for the column next to the noteheads, 1 for the next one to the left, …</param>
/// <param name="Midi">Sounding pitch, used to find the note a tie goes to.</param>
/// <param name="TieToNext">A tie runs from this note to the next note of the same pitch.</param>
internal sealed record PreparedNote(int Position, int? Accidental, int Side, int AccidentalColumn, int Midi, bool TieToNext);

/// <summary>Notes of one voice that start together on one staff (a chord or a single note), or a rest.</summary>
internal sealed class PreparedChord
{
    public required int PartIndex { get; init; }
    public required int Staff { get; init; }
    public required int Voice { get; init; }
    public required Rational Start { get; init; }

    /// <summary>How long the chord or rest lasts, in quarter notes (a whole-measure rest: the whole measure).</summary>
    public Rational Duration { get; init; }

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

    /// <summary>Lyrics sung on this chord, one per verse.</summary>
    public IReadOnlyList<ScoreLyric> Lyrics { get; init; } = Array.Empty<ScoreLyric>();

    /// <summary>Verses whose extender line ends at this chord.</summary>
    public IReadOnlyList<int> ExtendEnds { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Shift to the right of the onset, in staff spaces: one of two voices moves aside when their notes would touch (a
    /// second, or a unison that cannot share a notehead).
    /// </summary>
    public double XOffset { get; set; }

    /// <summary>
    /// For a rest: how far it moves up from its normal place, in staff positions (half spaces; negative is down). In two
    /// voices a rest moves away from the other voice's notes.
    /// </summary>
    public int RestShift { get; set; }

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

    /// <summary>Chord symbols of every part in this measure.</summary>
    public IReadOnlyList<(int PartIndex, ScoreChordSymbol Symbol)> ChordSymbols { get; init; } = Array.Empty<(int, ScoreChordSymbol)>();

    /// <summary>Length of the measure in quarter notes (the longest part, at least the time signature).</summary>
    public Rational Length { get; init; }

    public IReadOnlyList<MidMeasureClef> MidMeasureClefs { get; init; } = Array.Empty<MidMeasureClef>();
    public required List<Rational> Onsets { get; init; }

    /// <summary>Room before each onset for accidentals, in staff spaces.</summary>
    public required double[] Pre { get; init; }

    /// <summary>Space from each onset to the next (or to the barline), in staff spaces.</summary>
    public required double[] Segments { get; init; }

    /// <summary>
    /// The least each segment may be squeezed to: room for the notes and syllables, and never below
    /// <see cref="NotePreparation.MinimumGapScale"/> of the natural gap.
    /// </summary>
    public required double[] MinSegments { get; init; }

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

    /// <param name="lyricSize">Lyric font size in staff spaces, used to leave room for the syllables.</param>
    public static PreparedMeasure[] Prepare(ScoreProject project, int measureCount, ClefTimeline clefs, double lyricSize)
    {
        var measures = new PreparedMeasure[measureCount];
        var tiedFromPrevious = new HashSet<(int Part, int Staff, int Voice, int Midi)>();
        var chordsByMeasure = Enumerable.Range(0, measureCount).Select(_ => new List<PreparedChord>()).ToArray();
        var beamsByMeasure = Enumerable.Range(0, measureCount).Select(_ => new List<PreparedBeam>()).ToArray();
        var symbolsByMeasure = Enumerable.Range(0, measureCount).Select(_ => new List<(int, ScoreChordSymbol)>()).ToArray();
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
                symbolsByMeasure[m].AddRange(measure.ChordSymbols.Select(c => (p, c)));
                foreach (var change in measure.ClefChanges.Where(c => c.PositionNumerator > 0))
                    midClefs[m].Add(new MidMeasureClef(p, change.Staff, new Rational(change.PositionNumerator, change.PositionDenominator), change.Clef));
            }
        }

        for (var m = 0; m < measureCount; m++)
        {
            measures[m] = Space(chordsByMeasure[m], beamsByMeasure[m], midClefs[m], lengths[m] == default ? new Rational(4, 1) : lengths[m],
                lyricSize, symbolsByMeasure[m]);
        }

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
        public required List<(int Position, int? Accidental, int Midi, bool Tie)> Notes { get; init; }
        public required IReadOnlyList<ScoreLyric> Lyrics { get; init; }
        public required IReadOnlyList<int> ExtendEnds { get; init; }
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
            var staffRests = new List<PreparedChord>();

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
                    var rest = new PreparedChord
                    {
                        PartIndex = partIndex, Staff = staff, Voice = first.Voice, Start = Start(first), Value = value,
                        Duration = isMeasureRest ? Rational.Max(duration, nominal) : duration,
                        IsRest = true, IsMeasureRest = isMeasureRest, VoiceRole = role, OctaveShift = first.OctaveShift,
                        RightExtent = isMeasureRest ? 0 : Smufl.RestWidth(value.Type) + DotsWidth(value),
                    };
                    result.Add(rest);
                    staffRests.Add(rest);
                    continue;
                }

                var notes = new List<(int Position, int? Accidental, int Midi, bool Tie)>();
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
                    notes.Add((position, accidental, note.MidiPitch, note.TieToNext));
                }

                if (notes.Count == 0)
                    continue;

                pending.Add(new PendingChord
                {
                    First = first, Value = value, Role = role, Notes = notes,
                    Beams = chordEvents.Select(e => e.Beams).FirstOrDefault(b => b is { Count: > 0 }) ?? new List<BeamType>(),
                    Lyrics = chordEvents.Select(e => e.Lyrics).FirstOrDefault(l => l is { Count: > 0 }) ?? new List<ScoreLyric>(),
                    ExtendEnds = chordEvents.SelectMany(e => e.ExtendEnds).Distinct().ToList(),
                });
            }

            var groups = BeamGroups(pending);
            var built = new Dictionary<PendingChord, PreparedChord>();
            foreach (var chord in pending)
            {
                var stemUp = chord.GroupStemUp ?? StemUp(chord.Role, chord.Notes.Select(n => n.Position).ToList());
                built[chord] = BuildChord(partIndex, staff, chord.First.Voice, Start(chord.First), Duration(chord.First), chord.Value, stemUp, chord.Role,
                    chord.Notes, chord.First.OctaveShift, chord.Beams, chord.Lyrics, chord.ExtendEnds);
                result.Add(built[chord]);
            }

            beams.AddRange(groups.Select(g => new PreparedBeam(g.Select(c => built[c]).ToList(), g[0].GroupStemUp!.Value)));
            if (multiVoice)
                SeparateVoices(built.Values.ToList(), staffRests);
        }
    }

    /// <summary>
    /// Keeps two voices on one staff apart. A rest moves (by whole spaces) above the other voices' notes that sound
    /// during it if it belongs to the upper voice, below them if to a lower voice, keeping half a space clear. Where an
    /// upper-voice chord and a lower-voice chord start together and their notes are a second apart, or in unison with
    /// different noteheads or dots, one chord moves right: the upper (stem-up) one, past the lower one's notehead and
    /// dots; or the lower one when only it is dotted.
    /// </summary>
    private static void SeparateVoices(List<PreparedChord> chords, List<PreparedChord> rests)
    {
        foreach (var rest in rests.Where(r => r.VoiceRole != 0))
        {
            var end = rest.Start + rest.Duration;
            var others = chords.Where(c => c.Voice != rest.Voice && c.Start < end && c.Start + c.Duration > rest.Start)
                .SelectMany(c => c.Notes.Select(n => n.Position)).ToList();
            var type = rest.IsMeasureRest ? NoteType.Whole : rest.Value.Type;
            var anchor = type <= NoteType.Whole ? 6 : 4; // whole rests hang from the fourth line, others sit on the middle line
            var (bottom, top) = Smufl.RestBounds(type);
            var shift = rest.VoiceRole > 0 ? 4 : -4;
            if (others.Count > 0 && rest.VoiceRole > 0)
                shift = Math.Max(shift, EvenUp(others.Max() + 2 - anchor - (int)Math.Floor(bottom * 2)));
            else if (others.Count > 0)
                shift = Math.Min(shift, -EvenUp(anchor + (int)Math.Ceiling(top * 2) - (others.Min() - 2)));
            rest.RestShift = shift;
        }

        foreach (var onset in chords.GroupBy(c => c.Start))
        {
            foreach (var up in onset.Where(c => c.VoiceRole > 0))
            {
                foreach (var down in onset.Where(c => c.VoiceRole < 0))
                {
                    var shareable = Smufl.Notehead(up.Value.Type) == Smufl.Notehead(down.Value.Type) && up.Value.Dots == down.Value.Dots;
                    var touching = up.Notes.Any(u => down.Notes.Any(d => Math.Abs(u.Position - d.Position) == 1 || (u.Position == d.Position && !shareable)));
                    if (!touching) continue;

                    // The upper voice moves right, unless only the lower voice is dotted: then it moves instead, so the
                    // upper note does not cover its dot. When both are dotted the upper note clears the lower one's dots.
                    if (down.Value.Dots > 0 && up.Value.Dots == 0)
                        down.XOffset = Math.Max(down.XOffset, Smufl.NoteheadWidth(up.Value.Type));
                    else
                        up.XOffset = Math.Max(up.XOffset, Smufl.NoteheadWidth(down.Value.Type) + DotsWidth(down.Value));
                }
            }
        }
    }

    /// <summary>The smallest even number at least <paramref name="value"/>.</summary>
    private static int EvenUp(int value) => value % 2 == 0 ? value : value + 1;

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
        int partIndex, int staff, int voice, Rational start, Rational duration, NoteValue value, bool stemUp, int role,
        List<(int Position, int? Accidental, int Midi, bool Tie)> notes, int octaveShift, IReadOnlyList<BeamType> beams,
        IReadOnlyList<ScoreLyric> lyrics, IReadOnlyList<int> extendEnds)
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
            PartIndex = partIndex, Staff = staff, Voice = voice, Start = start, Duration = duration, Value = value, IsRest = false,
            StemUp = stemUp, VoiceRole = role, OctaveShift = octaveShift, Beams = beams, Lyrics = lyrics, ExtendEnds = extendEnds,
            Notes = sorted.Select((n, i) => new PreparedNote(n.Position, n.Accidental, sides[i], columns[i], n.Midi, n.Tie)).ToList(),
            LeftExtent = displacedLeft + accidentalRoom,
            RightExtent = headWidth + displacedRight + Math.Max(DotsWidth(value), flagRoom),
        };
    }

    /// <summary>
    /// Horizontal spacing: every onset in the measure (across all parts) gets a column, and the gap to the next onset
    /// grows with the time between them, so simultaneous notes line up in every part.
    /// </summary>
    private static PreparedMeasure Space(
        List<PreparedChord> chords, List<PreparedBeam> beams, List<MidMeasureClef> midClefs, Rational length, double lyricSize,
        List<(int, ScoreChordSymbol)> chordSymbols)
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
        var minSegments = new double[onsets.Count];
        var lyricLeft = new double[onsets.Count];
        var lyricRight = new double[onsets.Count];
        for (var i = 0; i < onsets.Count; i++)
        {
            foreach (var chord in chords.Where(c => c.Start == onsets[i] && !c.IsRest))
            {
                var (left, right) = LyricExtent(chord, lyricSize);
                (lyricLeft[i], lyricRight[i]) = (Math.Max(lyricLeft[i], left), Math.Max(lyricRight[i], right));
            }
        }

        for (var i = 0; i < onsets.Count; i++)
        {
            var atOnset = chords.Where(c => c.Start == onsets[i] && !c.IsMeasureRest).ToList();
            pre[i] = atOnset.Select(c => c.LeftExtent - c.XOffset).DefaultIfEmpty(0).Max();
            if (clefsAtOnsets.Any(c => c.Onset == onsets[i]))
                pre[i] += ClefChangeWidth;
            var next = i + 1 < onsets.Count ? onsets[i + 1] : length;
            var gap = SpaceFor(next - onsets[i]);
            var needed = atOnset.Select(c => c.XOffset + c.RightExtent + 0.3).DefaultIfEmpty(0).Max();
            segments[i] = Math.Max(gap, needed);
            minSegments[i] = Math.Max(MinimumGapScale * gap, needed);
        }

        // A syllable must clear the next syllable (or the barline): the gaps between the two onsets, held notes included,
        // grow in proportion until they are wide enough.
        for (var i = 0; i < onsets.Count; i++)
        {
            if (lyricRight[i] <= 0) continue;
            var next = i + 1;
            while (next < onsets.Count && lyricRight[next] <= 0) next++;
            var need = lyricRight[i] + (next < onsets.Count ? lyricLeft[next] + LyricGap * lyricSize : 0);
            Widen(segments, i, next, need);
            Widen(minSegments, i, next, need);
        }

        // Chord symbols of a staff must clear the next symbol (or the barline). They may fall between onsets (a chord
        // change during a held note), so the room is measured in time: the gaps that span the two symbols' beats grow.
        foreach (var line in chordSymbols.GroupBy(c => (c.Item1, c.Item2.Staff)))
        {
            var ordered = line.Select(c => c.Item2).OrderBy(Position).ToList();
            for (var k = 0; k < ordered.Count && onsets.Count > 0; k++)
            {
                var end = k + 1 < ordered.Count ? Position(ordered[k + 1]) : length;
                var width = TextMetrics.Width(ScoreLayoutEngine.ChordDisplay(ordered[k].Text), lyricSize) + ChordSymbolGap * lyricSize;
                WidenSpan(segments, onsets, length, Position(ordered[k]), end, width);
                WidenSpan(minSegments, onsets, length, Position(ordered[k]), end, width);
            }
        }

        for (var i = 0; i < onsets.Count; i++)
            minSegments[i] = Math.Min(minSegments[i], segments[i]);

        return new PreparedMeasure
        {
            Chords = chords, Beams = beams, MidMeasureClefs = clefsAtOnsets, Onsets = onsets, Pre = pre, Segments = segments, MinSegments = minSegments,
            ChordSymbols = chordSymbols, Length = length,
        };
    }

    /// <summary>
    /// How far a gap between notes may shrink to keep the source file's line breaks (e.g. Finale's layout), as a factor
    /// of the natural gap. Beyond that the measures are spread over more systems.
    /// </summary>
    public const double MinimumGapScale = 0.6;

    /// <summary>
    /// Least gap between two syllables, in ems. Like Finale's smart hyphens, a hyphen is only drawn where the gap is wider.
    /// </summary>
    public const double LyricGap = 0.3;

    private static Rational Position(ScoreChordSymbol symbol) => new(symbol.PositionNumerator, symbol.PositionDenominator);

    /// <summary>
    /// Widens the gaps that overlap the time span from <paramref name="from"/> to <paramref name="to"/> by one factor until
    /// the span is <paramref name="need"/> wide. A gap runs from its onset to the next onset (or the end of the measure).
    /// </summary>
    private static void WidenSpan(double[] gaps, List<Rational> onsets, Rational length, Rational from, Rational to, double need)
    {
        double Overlap(int j)
        {
            var (start, end) = (onsets[j], j + 1 < onsets.Count ? onsets[j + 1] : length);
            var span = (end - start).ToDouble();
            if (span <= 0) return 0;
            var overlap = (Rational.Max(Rational.Zero, (to < end ? to : end) - (from > start ? from : start))).ToDouble();
            return overlap / span;
        }

        var width = 0.0;
        for (var j = 0; j < onsets.Count; j++) width += gaps[j] * Overlap(j);
        if (width <= 0 || width >= need) return;

        var factor = need / width;
        for (var j = 0; j < onsets.Count; j++)
        {
            if (Overlap(j) > 0) gaps[j] *= factor;
        }
    }

    /// <summary>Least gap after a chord symbol before the next one, in ems.</summary>
    public const double ChordSymbolGap = 0.5;

    /// <summary>Widens gaps <paramref name="from"/> to <paramref name="to"/> − 1 in proportion until they add up to <paramref name="need"/>.</summary>
    private static void Widen(double[] gaps, int from, int to, double need)
    {
        var sum = 0.0;
        for (var k = from; k < to; k++) sum += gaps[k];
        if (sum >= need) return;
        for (var k = from; k < to; k++)
            gaps[k] += sum > 0 ? (need - sum) * gaps[k] / sum : (need - sum) / (to - from);
    }

    /// <summary>
    /// How far a chord's syllables reach left and right of its onset, in staff spaces. Syllables are centred under the
    /// notehead; a syllable with an extender line starts at the notehead instead.
    /// </summary>
    public static (double Left, double Right) LyricExtent(PreparedChord chord, double lyricSize)
    {
        var head = Smufl.NoteheadWidth(chord.Value.Type);
        var (left, right) = (0.0, 0.0);
        foreach (var lyric in chord.Lyrics)
        {
            var width = TextMetrics.Width(lyric.Text, lyricSize);
            if (lyric.Extend)
            {
                right = Math.Max(right, width);
            }
            else
            {
                left = Math.Max(left, width / 2 - head / 2);
                right = Math.Max(right, head / 2 + width / 2);
            }
        }

        return (left, right);
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
