namespace ScoreStudio.Layout;

/// <summary>A staff in a measure on the page: where a click that hits no note puts the input cursor.</summary>
public sealed record StaffMeasureHit(int PartIndex, int Staff, int MeasureIndex);

/// <summary>Finds what is drawn at a point of a page (in mm), for selecting with the mouse.</summary>
public static class LayoutHitTest
{
    /// <summary>
    /// The chord or rest whose box holds the point, allowing <paramref name="tolerance"/> mm around it; the nearest box
    /// centre wins when boxes overlap (two voices). Null when nothing is there.
    /// </summary>
    public static ChordBox? FindChord(PageLayout page, double x, double y, double tolerance) =>
        page.Chords.Where(c => c.Contains(x, y, tolerance))
            .OrderBy(c => Math.Pow(x - (c.Left + c.Right) / 2, 2) + Math.Pow(y - (c.Top + c.Bottom) / 2, 2))
            .FirstOrDefault();

    /// <summary>
    /// The staff and measure at the point: the nearest staff of the system whose band (staff and half the gap to its
    /// neighbours) holds the point, and the measure whose barlines enclose it. Null outside every system.
    /// </summary>
    public static StaffMeasureHit? FindStaffMeasure(PageLayout page, double x, double y, double staffSpace)
    {
        foreach (var system in page.Systems)
        {
            var measure = system.Measures.FirstOrDefault(m => x >= m.Left && x <= m.Right);
            if (measure is null || system.Staves.Count == 0) continue;

            var margin = 4 * staffSpace;
            if (y < system.Staves[0].Top - margin || y > system.Staves[^1].Top + 4 * staffSpace + margin) continue;

            var staff = system.Staves.OrderBy(s => Math.Abs(y - (s.Top + 2 * staffSpace))).First();
            return new StaffMeasureHit(staff.PartIndex, staff.StaffIndex, measure.MeasureIndex);
        }

        return null;
    }
}
