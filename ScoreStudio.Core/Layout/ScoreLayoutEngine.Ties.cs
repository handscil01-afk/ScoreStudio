namespace ScoreStudio.Layout;

public static partial class ScoreLayoutEngine
{
    /// <summary>A tie that left the end of a system; it comes back in front of its note in the next system.</summary>
    private sealed record PendingTie(int PartIndex, int Staff, int Voice, int Midi, bool Above);

    /// <summary>Thickness of a tie in its middle (Bravura's tieMidpointThickness), in staff spaces.</summary>
    private const double TieThickness = 0.22;

    /// <summary>
    /// Draws the ties of one system. A tie goes from a note to the next note of the same pitch on the same staff,
    /// preferring the same voice. Ties curve away from the stem: above for stems down, below for stems up; in a chord
    /// the upper half of the notes tie above and the lower half below, and with two voices the upper voice ties above.
    /// A tie whose note is in the next system runs to the end of this one and starts again before that note.
    /// </summary>
    private static void DrawTies(
        List<DrawItem> items, List<PlacedChord> placed, List<StaffSlot> slots, double[] staffTops,
        double contentLeft, double systemRight, List<PendingTie> pending, double sp)
    {
        var ordered = placed.OrderBy(p => p.Measure).ThenBy(p => p.Chord.Start).ToList();

        // Ties coming in from the previous system end at the first matching note here.
        foreach (var tie in pending)
        {
            var slot = slots.FindIndex(s => s.PartIndex == tie.PartIndex && s.StaffIndex == tie.Staff);
            if (slot < 0) continue;
            var target = TieTarget(ordered, slot, null, tie.Voice, tie.Midi);
            if (target is null) continue;

            var note = target.Chord.Notes.First(n => n.Midi == tie.Midi);
            var y = StaffY(staffTops[slot], note.Position, sp) + (tie.Above ? -0.6 : 0.6) * sp;
            var endX = target.X + note.Side * Smufl.NoteheadWidth(target.Chord.Value.Type) * sp - 0.15 * sp;
            AddTie(items, Math.Max(contentLeft, endX - 3 * sp), y, endX, y, tie.Above, sp);
        }

        pending.Clear();

        foreach (var from in ordered)
        {
            var chord = from.Chord;
            var head = Smufl.NoteheadWidth(chord.Value.Type) * sp;
            var notes = chord.Notes;
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (!note.TieToNext) continue;

                var above = TieAbove(chord, i);
                var single = notes.Count == 1;
                var offset = (above ? -1 : 1) * (single ? 0.6 : 0.25) * sp;
                var startX = from.X + note.Side * head + (single ? 0.6 * head : head + 0.15 * sp);
                var startY = StaffY(staffTops[from.Slot], note.Position, sp) + offset;

                var target = TieTarget(ordered, from.Slot, from, chord.Voice, note.Midi);
                if (target is null)
                {
                    if (ordered.Any(p => p.Slot == from.Slot && p.Chord.Voice == chord.Voice && IsAfter(p, from)))
                    {
                        // The voice goes on without this pitch: the source's tie leads nowhere, so show a short one.
                        AddTie(items, startX, startY, startX + 2 * sp, startY, above, sp);
                        continue;
                    }

                    AddTie(items, startX, startY, systemRight - 0.5 * sp, startY, above, sp);
                    var slot = slots[from.Slot];
                    pending.Add(new PendingTie(slot.PartIndex, slot.StaffIndex, chord.Voice, note.Midi, above));
                    continue;
                }

                var targetNote = target.Chord.Notes.First(n => n.Midi == note.Midi);
                var targetHead = Smufl.NoteheadWidth(target.Chord.Value.Type) * sp;
                var endX = target.X + targetNote.Side * targetHead + (single ? 0.4 * targetHead : -0.15 * sp);
                var endY = StaffY(staffTops[target.Slot], targetNote.Position, sp) + offset;
                AddTie(items, startX, startY, Math.Max(endX, startX + sp), endY, above, sp);
            }
        }
    }

    /// <summary>
    /// The note a tie ends on: the next chord of the same voice if it has the pitch; otherwise a chord of another voice
    /// on the staff that has it and comes no later than that next chord. Null when there is none in this system.
    /// <paramref name="from"/> null means from the start of the system.
    /// </summary>
    private static PlacedChord? TieTarget(List<PlacedChord> ordered, int slot, PlacedChord? from, int voice, int midi)
    {
        var later = ordered.Where(p => p.Slot == slot && (from is null || IsAfter(p, from))).ToList();
        var next = later.FirstOrDefault(p => p.Chord.Voice == voice);
        if (next is not null && next.Chord.Notes.Any(n => n.Midi == midi)) return next;
        return later.FirstOrDefault(p => p.Chord.Notes.Any(n => n.Midi == midi) && (next is null || !IsAfter(p, next)));
    }

    /// <summary>Which way the tie of note <paramref name="index"/> (lowest note first) of a chord curves.</summary>
    private static bool TieAbove(PreparedChord chord, int index)
    {
        if (chord.VoiceRole != 0) return chord.VoiceRole > 0;
        var count = chord.Notes.Count;
        if (count == 1 || (count % 2 == 1 && index == count / 2)) return !chord.StemUp;
        return index >= count / 2;
    }

    /// <summary>A tie from (x1, y1) to (x2, y2): flat and low for short ties, up to one space high for long ones.</summary>
    private static void AddTie(List<DrawItem> items, double x1, double y1, double x2, double y2, bool above, double sp)
    {
        var width = x2 - x1;
        if (width <= 0) return;
        var height = Math.Clamp(width * 0.12, 0.45 * sp, 1.0 * sp) * (above ? -1 : 1);

        // Both control points at 4/3 of the height put the curve's peak at the wanted height.
        var control = height * 4 / 3;
        items.Add(new CurveItem(x1, y1, x1 + width / 4, y1 + control, x2 - width / 4, y2 + control, x2, y2, TieThickness * sp));
    }
}
