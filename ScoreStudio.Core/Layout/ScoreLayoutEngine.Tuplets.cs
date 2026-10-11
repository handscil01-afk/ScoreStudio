using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Layout;

public static partial class ScoreLayoutEngine
{
    /// <summary>Em size of tuplet numbers, in staff spaces (smaller than the noteheads' 4).</summary>
    private const double TupletNumberSize = 3.2;

    /// <summary>From the notes' outer edge (heads, stems, beams, rests) to the bracket line or the number's middle.</summary>
    private const double TupletGap = 1.0;

    /// <summary>Length of the bracket's end hooks, towards the notes.</summary>
    private const double TupletHook = 0.6;

    /// <summary>Room left and right of the number in the bracket.</summary>
    private const double TupletNumberRoom = 0.3;

    /// <summary>Least distance between a tuplet number or bracket and the staff's outer line.</summary>
    private const double TupletStaffClearance = 0.5;

    /// <summary>How far a bracket may slope from one end to the other.</summary>
    private const double TupletMaxSlope = 1.0;

    /// <summary>
    /// Draws the number and bracket of every tuplet in the measure (docs/SPRINT4F_TUPLETS.md): on the stem side unless
    /// the tuplet says (the other side when the number would land on other notes there), following the slope of its first and last notes, clear of everything in it and outside the
    /// staff. Short tuplets go first, so a tuplet around them goes outside their marks. The chords' reach grows by the
    /// marks, so chord symbols and lyrics keep clear of them.
    /// </summary>
    private static void DrawTuplets(
        List<DrawItem> items, PreparedMeasure measure, List<StaffSlot> slots, double[] staffTops, int measureIndex,
        List<PlacedChord> placed, List<ChordBox> boxes, double sp)
    {
        if (measure.Tuplets.Count == 0) return;

        // How far each rest reaches, grown by the tuplet marks drawn over it.
        var restReach = new Dictionary<ChordBox, (double Top, double Bottom)>();
        var ordered = measure.Tuplets
            .OrderBy(t => new Rational(t.Tuplet.DurationNumerator, t.Tuplet.DurationDenominator))
            .ThenBy(t => new Rational(t.Tuplet.PositionNumerator, t.Tuplet.PositionDenominator));
        foreach (var (partIndex, tuplet) in ordered)
        {
            var slot = slots.FindIndex(s => s.PartIndex == partIndex && s.StaffIndex == tuplet.Staff);
            if (slot < 0) continue;
            var start = new Rational(tuplet.PositionNumerator, tuplet.PositionDenominator);
            var end = start + new Rational(tuplet.DurationNumerator, tuplet.DurationDenominator);
            bool Inside(Rational time) => time >= start && time < end;

            var chords = Enumerable.Range(0, placed.Count)
                .Where(i => placed[i].Measure == measureIndex && placed[i].Chord.PartIndex == partIndex && placed[i].Chord.Staff == tuplet.Staff
                    && placed[i].Chord.Voice == tuplet.Voice && Inside(placed[i].Chord.Start))
                .ToList();
            var rests = boxes.Where(b => b.IsRest && b.MeasureIndex == measureIndex && b.PartIndex == partIndex && b.Staff == tuplet.Staff
                && b.Voice == tuplet.Voice && Inside(b.Start)).ToList();
            if (chords.Count + rests.Count == 0) continue;

            var elements = chords.Select(i =>
                {
                    var chord = placed[i].Chord;
                    var head = Smufl.NoteheadWidth(chord.Value.Type) * sp;
                    var left = placed[i].X + (chord.Notes.Any(n => n.Side < 0) ? -head : 0);
                    var right = placed[i].X + head * (chord.Notes.Any(n => n.Side > 0) ? 2 : 1);
                    return (Start: chord.Start, Left: left, Right: right, placed[i].Top, placed[i].Bottom);
                })
                .Concat(rests.Select(r => (r.Start, r.Left, r.Right,
                    Top: restReach.TryGetValue(r, out var reach) ? reach.Top : r.Top, Bottom: restReach.TryGetValue(r, out reach) ? reach.Bottom : r.Bottom)))
                .OrderBy(e => e.Start).ToList();

            var stemChords = chords.Select(i => placed[i].Chord).OrderBy(c => c.Start).ToList();
            var above = tuplet.Above ?? (stemChords.Count == 0 || stemChords.Count(c => c.StemUp) * 2 >= stemChords.Count);
            var bracket = tuplet.Bracket ?? !IsOneBeamGroup(stemChords, rests.Count);
            var digits = tuplet.ShowNumber switch
            {
                TupletNumber.None => "",
                TupletNumber.Both => $"{tuplet.Actual}:{tuplet.Normal}",
                _ => $"{tuplet.Actual}",
            };
            if (digits.Length == 0 && !bracket) continue;

            // The line: through the first and last elements' outer edges (at most a space of slope), then moved out until
            // every element and the staff are clear of it.
            var (x1, x2) = (elements[0].Left, elements[^1].Right);
            var middle = (x1 + x2) / 2;
            var scale = TupletNumberSize / 4;
            var numberWidth = digits.Sum(c => Smufl.TupletGlyphWidth(TupletGlyph(c))) * scale * sp;
            var halfHeight = Smufl.TupletDigitHeight * scale * sp / 2;
            (double Y1, double Slope) Place(bool up)
            {
                var away = up ? -1 : 1;
                double Edge((Rational Start, double Left, double Right, double Top, double Bottom) e) => up ? e.Top : e.Bottom;
                var slope = elements.Count > 1 && x2 - x1 > 1e-9
                    ? Math.Clamp(Edge(elements[^1]) - Edge(elements[0]), -TupletMaxSlope * sp, TupletMaxSlope * sp) / (x2 - x1)
                    : 0.0;
                var staffEdge = up ? staffTops[slot] - (TupletStaffClearance + TupletHook) * sp : staffTops[slot] + (4 + TupletStaffClearance + TupletHook) * sp;
                var y = Edge(elements[0]) + away * TupletGap * sp;
                var push = 0.0;
                foreach (var e in elements)
                {
                    foreach (var x in new[] { e.Left, e.Right })
                        push = Math.Max(push, away * (Edge(e) + away * TupletGap * sp - (y + slope * (x - x1))));
                }

                foreach (var x in new[] { x1, x2 })
                    push = Math.Max(push, away * (staffEdge - (y + slope * (x - x1))));
                return (y + away * push, slope);
            }

            // The stem side (or the source's side), unless the number would land on other notes there (another voice or
            // staff) and the other side is clear. Finale writes a side for every tuplet, so it is a preference too.
            var (y1, slope) = Place(above);
            bool NumberHits((double Y1, double Slope) line)
            {
                var y = line.Y1 + line.Slope * (middle - x1);
                return HitsNotes(items, middle - numberWidth / 2, middle + numberWidth / 2, y - halfHeight, y + halfHeight, sp);
            }

            if (digits.Length > 0 && NumberHits((y1, slope)) && !NumberHits(Place(!above)))
            {
                above = !above;
                (y1, slope) = Place(above);
            }

            var direction = above ? -1 : 1; // away from the notes
            double Line(double x) => y1 + slope * (x - x1);
            if (digits.Length > 0)
            {
                var x = middle - numberWidth / 2;
                var baseline = Line(middle) + halfHeight;
                foreach (var c in digits)
                {
                    items.Add(new GlyphItem(TupletGlyph(c), x, baseline, TupletNumberSize * sp));
                    x += Smufl.TupletGlyphWidth(TupletGlyph(c)) * scale * sp;
                }
            }

            var thickness = Smufl.TupletBracketThickness * sp;
            if (bracket)
            {
                var hook = -direction * TupletHook * sp;
                items.Add(new LineItem(x1, Line(x1) + hook, x1, Line(x1), thickness));
                items.Add(new LineItem(x2, Line(x2) + hook, x2, Line(x2), thickness));
                if (digits.Length == 0)
                {
                    items.Add(new LineItem(x1, Line(x1), x2, Line(x2), thickness));
                }
                else
                {
                    var (gapLeft, gapRight) = (middle - numberWidth / 2 - TupletNumberRoom * sp, middle + numberWidth / 2 + TupletNumberRoom * sp);
                    if (gapLeft > x1) items.Add(new LineItem(x1, Line(x1), gapLeft, Line(gapLeft), thickness));
                    if (gapRight < x2) items.Add(new LineItem(gapRight, Line(gapRight), x2, Line(x2), thickness));
                }
            }

            // The marks' outer edge: the number's far side (or the bracket line).
            var reachEdge = (double x) => Line(x) + direction * (digits.Length > 0 ? halfHeight : thickness / 2);
            foreach (var i in chords)
            {
                var edge = reachEdge(placed[i].X);
                placed[i] = above ? placed[i] with { Top = Math.Min(placed[i].Top, edge) } : placed[i] with { Bottom = Math.Max(placed[i].Bottom, edge) };
            }

            foreach (var r in rests)
            {
                var current = restReach.TryGetValue(r, out var reach) ? reach : (r.Top, r.Bottom);
                var edge = reachEdge(r.Left);
                restReach[r] = above ? (Math.Min(current.Item1, edge), current.Item2) : (current.Item1, Math.Max(current.Item2, edge));
            }
        }
    }

    /// <summary>Whether a box (mm) touches a notehead, rest, stem or beam already drawn.</summary>
    private static bool HitsNotes(List<DrawItem> items, double left, double right, double top, double bottom, double sp)
    {
        bool Overlaps(double l, double r, double t, double b) => Math.Min(right, r) - Math.Max(left, l) > 0 && Math.Min(bottom, b) - Math.Max(top, t) > 0;
        foreach (var item in items)
        {
            switch (item)
            {
                case GlyphItem g when g.X < right && g.X + 1.5 * sp > left:
                    if (g.Glyph is Smufl.NoteheadBlack or Smufl.NoteheadHalf or Smufl.NoteheadWhole or Smufl.NoteheadDoubleWhole
                        && Overlaps(g.X, g.X + 1.2 * sp, g.Y - 0.5 * sp, g.Y + 0.5 * sp))
                        return true;
                    if (g.Glyph >= Smufl.RestDoubleWhole && g.Glyph <= Smufl.RestDoubleWhole + 7)
                    {
                        var type = (NoteType)(g.Glyph - Smufl.RestDoubleWhole);
                        var (low, high) = Smufl.RestBounds(type);
                        if (Overlaps(g.X, g.X + Smufl.RestWidth(type) * sp, g.Y - high * sp, g.Y - low * sp)) return true;
                    }

                    break;
                case LineItem l when l.X1 == l.X2 && Math.Abs(l.Thickness - Smufl.StemThickness * sp) < 1e-9:
                    if (Overlaps(l.X1 - l.Thickness / 2, l.X1 + l.Thickness / 2, Math.Min(l.Y1, l.Y2), Math.Max(l.Y1, l.Y2))) return true;
                    break;
                case BeamItem b when b.X1 < right && b.X2 > left:
                    var (from, to) = (Math.Max(b.X1, left), Math.Min(b.X2, right));
                    double Y(double x) => b.Y1 + (b.Y2 - b.Y1) * (x - b.X1) / (b.X2 - b.X1);
                    if (Overlaps(from, to, Math.Min(Y(from), Y(to)), Math.Max(Y(from), Y(to)) + b.Thickness)) return true;
                    break;
            }
        }

        return false;
    }

    /// <summary>Whether a tuplet's notes are exactly one beam group: no rests, beamed, the group starting and ending with it.</summary>
    private static bool IsOneBeamGroup(List<PreparedChord> chords, int rests)
    {
        if (rests > 0 || chords.Count < 2 || chords.Any(c => !c.IsBeamed)) return false;
        return chords[0].Beams[0] == BeamType.Begin && chords[^1].Beams[0] == BeamType.End
            && chords.Skip(1).SkipLast(1).All(c => c.Beams[0] == BeamType.Continue);
    }

    private static char TupletGlyph(char c) => c == ':' ? Smufl.TupletColon : (char)(Smufl.Tuplet0 + (c - '0'));
}
