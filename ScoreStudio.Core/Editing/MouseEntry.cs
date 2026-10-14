using System.Globalization;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;

namespace ScoreStudio.Editing;

/// <summary>
/// Where a click on a staff enters a note: the place in the score, the written pitch (diatonic step number from C0, in
/// the key), its name, and the preview to draw (a grey notehead with leger lines) in page mm.
/// </summary>
public sealed record MouseEntryTarget(EditPosition At, int Diatonic, string PitchName, IReadOnlyList<DrawItem> Preview);

/// <summary>Turns a pointer position on a page into a note entry target (see docs/SPRINT4_EDITING.md, mouse entry).</summary>
public static class MouseEntry
{
    /// <summary>Highest and lowest staff positions (0 = bottom line, 8 = top line) a click can enter: five leger lines out.</summary>
    private const int LowestPosition = -10;
    private const int HighestPosition = 18;

    /// <summary>
    /// The target at (<paramref name="x"/>, <paramref name="y"/>) mm on page <paramref name="pageIndex"/>, for voice 1:
    /// the pitch from the height (to the nearest line or space); the time from the column under the pointer (an entered
    /// chord or rest there, else the next one to the right, else the end of what the measure holds). Null off the staves,
    /// or when the measure is already full and the pointer is past its last chord.
    /// </summary>
    public static MouseEntryTarget? Find(ScoreLayout layout, int pageIndex, ScoreProject project, NoteType inputType, double x, double y)
    {
        if (pageIndex < 0 || pageIndex >= layout.Pages.Count) return null;
        var page = layout.Pages[pageIndex];
        var sp = layout.StaffSpace;
        if (LayoutHitTest.FindStaffMeasure(page, x, y, sp) is not { } hit) return null;

        var system = page.Systems.First(s => s.Measures.Any(m => m.MeasureIndex == hit.MeasureIndex && x >= m.Left && x <= m.Right));
        var measureBox = system.Measures.First(m => m.MeasureIndex == hit.MeasureIndex);
        var staffTop = system.Staves.First(s => s.PartIndex == hit.PartIndex && s.StaffIndex == hit.Staff).Top;
        var measure = project.Parts[hit.PartIndex].Measures[hit.MeasureIndex];

        // Time: the column under the pointer, else the next column, else after what is entered.
        var boxes = page.Chords.Where(c => c.PartIndex == hit.PartIndex && c.Staff == hit.Staff && c.MeasureIndex == hit.MeasureIndex && c.Voice == 1)
            .OrderBy(c => c.Start).ToList();
        Rational start;
        double headX;
        if ((boxes.FirstOrDefault(c => x >= c.Left - 0.6 * sp && x <= c.Right + 0.6 * sp) ?? boxes.FirstOrDefault(c => c.Left > x)) is { } column)
        {
            (start, headX) = (column.Start, column.Left);
        }
        else
        {
            start = EndOfVoice(measure, hit.Staff);
            if (start >= ScoreEditor.MeasureLength(measure)) return null;
            headX = boxes.Count > 0 ? boxes[^1].Right + 1.2 * sp : measureBox.Left + 1.2 * sp;
        }

        // Pitch: the nearest line or space, in the clef at that point.
        var position = Math.Clamp((int)Math.Round((staffTop + 4 * sp - y) / (sp / 2)), LowestPosition, HighestPosition);
        var clef = new ClefTimeline(project).ClefAt(hit.PartIndex, hit.Staff, hit.MeasureIndex, start);
        var diatonic = Notation.BottomLineDiatonic(clef) + position;
        if (diatonic < 0) return null;

        var step = diatonic % 7;
        var alter = Notation.KeyAlter(measure.KeyFifths, step);
        var name = $"{"CDEFGAB"[step]}{(alter > 0 ? "#" : alter < 0 ? "b" : "")}{(diatonic / 7).ToString(CultureInfo.InvariantCulture)}";

        var headY = staffTop + (8 - position) * sp / 2;
        var head = inputType switch
        {
            NoteType.Whole or NoteType.Breve => Smufl.NoteheadWhole,
            NoteType.Half => Smufl.NoteheadHalf,
            _ => Smufl.NoteheadBlack,
        };
        var headWidth = Smufl.NoteheadWidth(inputType) * sp;
        var preview = new List<DrawItem> { new GlyphItem(head, headX, headY, 4 * sp) };
        var (legerLeft, legerRight) = (headX - Smufl.LegerLineExtension * sp, headX + headWidth + Smufl.LegerLineExtension * sp);
        for (var p = -2; p >= position; p -= 2)
            preview.Add(new LineItem(legerLeft, staffTop + (8 - p) * sp / 2, legerRight, staffTop + (8 - p) * sp / 2, Smufl.LegerLineThickness * sp));
        for (var p = 10; p <= position; p += 2)
            preview.Add(new LineItem(legerLeft, staffTop + (8 - p) * sp / 2, legerRight, staffTop + (8 - p) * sp / 2, Smufl.LegerLineThickness * sp));

        return new MouseEntryTarget(new EditPosition(hit.PartIndex, hit.Staff, 1, hit.MeasureIndex, start), diatonic, name, preview);
    }

    /// <summary>
    /// The written pitch (diatonic step number from C0) at a pointer height on a staff, in the clef there, or null off the
    /// staves; Speedy Entry puts its crossbar there when the staff is clicked.
    /// </summary>
    public static int? DiatonicAt(ScoreLayout layout, int pageIndex, ScoreProject project, double x, double y)
    {
        if (pageIndex < 0 || pageIndex >= layout.Pages.Count) return null;
        var page = layout.Pages[pageIndex];
        var sp = layout.StaffSpace;
        if (LayoutHitTest.FindStaffMeasure(page, x, y, sp) is not { } hit) return null;
        var system = page.Systems.First(s => s.Measures.Any(m => m.MeasureIndex == hit.MeasureIndex && x >= m.Left && x <= m.Right));
        var staffTop = system.Staves.First(s => s.PartIndex == hit.PartIndex && s.StaffIndex == hit.Staff).Top;
        var position = Math.Clamp((int)Math.Round((staffTop + 4 * sp - y) / (sp / 2)), LowestPosition, HighestPosition);
        var clef = new ClefTimeline(project).ClefAt(hit.PartIndex, hit.Staff, hit.MeasureIndex, Rational.Zero);
        var diatonic = Notation.BottomLineDiatonic(clef) + position;
        return diatonic < 0 ? null : diatonic;
    }

    /// <summary>Where voice 1 of a staff ends in a measure: after its last chord or rest, or at the start.</summary>
    public static Rational EndOfVoice(ScoreMeasure measure, int staff) =>
        measure.Events.Where(e => e.Staff == staff && e.Voice == 1)
            .Select(e => new Rational(e.PositionNumerator, e.PositionDenominator) + new Rational(e.DurationNumerator, e.DurationDenominator))
            .DefaultIfEmpty(Rational.Zero).Max();
}
