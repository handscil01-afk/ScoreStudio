using ScoreStudio.Layout;
using ScoreStudio.Models;

namespace ScoreStudio.Editing;

/// <summary>Where the screen marks the selection (a box) and the input cursor (a vertical bar), page by page, in mm.</summary>
/// <param name="SelectionPage">Index of the page holding the selection, or −1.</param>
/// <param name="CursorPage">Index of the page holding the cursor, or −1 when its measure is not drawn.</param>
public sealed record EditMarks(int SelectionPage, ChordBox? Selection, int CursorPage, double CursorX, double CursorTop, double CursorBottom)
{
    /// <summary>
    /// Finds the selected chord's box and the cursor's place: before the chord or rest that starts at the cursor, else
    /// just after the last one before it, else at the start of the measure; spanning the cursor's staff.
    /// </summary>
    public static EditMarks Find(ScoreLayout layout, EditPosition? selection, EditPosition cursor)
    {
        var sp = layout.StaffSpace;
        var (selectionPage, selectionBox) = (-1, (ChordBox?)null);
        if (selection is { } s)
        {
            for (var p = 0; p < layout.Pages.Count && selectionBox is null; p++)
            {
                selectionBox = layout.Pages[p].Chords.FirstOrDefault(c => c.PartIndex == s.Part && c.Staff == s.Staff && c.Voice == s.Voice
                    && c.MeasureIndex == s.Measure && c.Start == s.Position);
                if (selectionBox is not null) selectionPage = p;
            }
        }

        for (var p = 0; p < layout.Pages.Count; p++)
        {
            var page = layout.Pages[p];
            foreach (var system in page.Systems)
            {
                var measure = system.Measures.FirstOrDefault(m => m.MeasureIndex == cursor.Measure);
                var staff = system.Staves.FirstOrDefault(st => st.PartIndex == cursor.Part && st.StaffIndex == cursor.Staff);
                if (measure is null || staff is null) continue;

                var boxes = page.Chords.Where(c => c.PartIndex == cursor.Part && c.Staff == cursor.Staff && c.MeasureIndex == cursor.Measure)
                    .OrderBy(c => c.Start).ThenBy(c => c.Left).ToList();
                var x = boxes.FirstOrDefault(c => c.Start == cursor.Position) is { } at ? at.Left - 0.4 * sp
                    : boxes.LastOrDefault(c => c.Start < cursor.Position) is { } before ? before.Right + 0.6 * sp
                    : measure.Left + 0.6 * sp;
                return new EditMarks(selectionPage, selectionBox, p, Math.Min(x, measure.Right - 0.3 * sp), staff.Top - sp, staff.Top + 5 * sp);
            }
        }

        return new EditMarks(selectionPage, selectionBox, -1, 0, 0, 0);
    }
}

/// <summary>Speedy Entry's crossbar on screen: a short bar at the cursor, at the crossbar's height, with leger lines.</summary>
public static class CrossbarMark
{
    /// <summary>The page index and the items to draw, or null when the cursor's measure is not drawn.</summary>
    public static (int Page, IReadOnlyList<DrawItem> Items)? Find(ScoreLayout layout, ScoreProject project, EditPosition cursor, int diatonic)
    {
        var marks = EditMarks.Find(layout, null, cursor);
        if (marks.CursorPage < 0) return null;
        var sp = layout.StaffSpace;
        var staffTop = marks.CursorTop + sp; // EditMarks spans a space above and below the staff
        var clef = new ClefTimeline(project).ClefAt(cursor.Part, cursor.Staff, cursor.Measure, cursor.Position);
        var position = diatonic - Notation.BottomLineDiatonic(clef);
        double Y(int p) => staffTop + (8 - p) * sp / 2;

        var (left, right) = (marks.CursorX - 0.4 * sp, marks.CursorX + 1.8 * sp);
        var items = new List<DrawItem> { new LineItem(left, Y(position), right, Y(position), 0.35 * sp) };
        for (var p = -2; p >= position; p -= 2) items.Add(new LineItem(left, Y(p), right, Y(p), Smufl.LegerLineThickness * sp));
        for (var p = 10; p <= position; p += 2) items.Add(new LineItem(left, Y(p), right, Y(p), Smufl.LegerLineThickness * sp));
        return (marks.CursorPage, items);
    }
}
