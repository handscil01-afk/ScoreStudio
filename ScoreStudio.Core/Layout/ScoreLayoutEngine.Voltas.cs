namespace ScoreStudio.Layout;

public static partial class ScoreLayoutEngine
{
    /// <summary>Font size of a 1st/2nd ending's number, in staff spaces.</summary>
    private const double VoltaTextSize = 2.0;

    /// <summary>
    /// Draws the 1st/2nd ending brackets of one system above its top staff, clear of its notes and chord symbols. A
    /// bracket starts with a hook and its number ("1.", or the source's own text) and ends with a hook unless it is open;
    /// a bracket that goes on into the next system stops at the end of this one and comes back there without a number.
    /// </summary>
    private static void DrawVoltas(
        List<DrawItem> items, PlannedSystem plan, List<MeasureInfo> measures, List<MeasureLayout> layouts,
        List<PlacedChord> placed, double staffTop, double symbolTop, double sp)
    {
        if (layouts.Count == 0) return;
        var first = layouts[0].MeasureIndex;
        var last = layouts[^1].MeasureIndex;

        // Brackets that start in this system, and one that started earlier and runs into it.
        var spans = new List<(int Start, int End, bool StartsHere, bool EndsHere, string Text)>();
        for (var m = 0; m <= last; m++)
        {
            if (measures[m].Volta is not { } volta) continue;
            var end = m + volta.MeasureCount - 1;
            if (end < first) continue;
            var text = string.IsNullOrWhiteSpace(volta.Text) ? volta.Numbers.Trim() + "." : volta.Text.Trim();
            spans.Add((Math.Max(m, first), Math.Min(end, last), m >= first, end <= last && !volta.OpenEnd, text));
        }

        if (spans.Count == 0) return;

        var highest = placed.Where(p => p.Slot == 0).Select(p => p.Top).DefaultIfEmpty(staffTop).Min();
        // The number hangs below the bracket line, so the line sits a number's height above everything under it.
        var below = Math.Min(highest - 0.5 * sp, symbolTop - 0.4 * sp);
        var y = Math.Min(staffTop - 3.5 * sp, below - VoltaTextSize * sp);
        var thickness = Smufl.ThinBarlineThickness * sp;
        var hook = 2.0 * sp;
        foreach (var (start, end, startsHere, endsHere, text) in spans)
        {
            var x1 = layouts.First(l => l.MeasureIndex == start).Left + (startsHere ? 0.5 * sp : 0);
            var x2 = layouts.First(l => l.MeasureIndex == end).Right - (endsHere ? 0.5 * sp : 0);
            items.Add(new LineItem(x1, y, x2, y, thickness));
            if (startsHere)
            {
                items.Add(new LineItem(x1, y, x1, y + hook, thickness));
                items.Add(new TextItem(text, x1 + 0.5 * sp, y + VoltaTextSize * sp * 0.9, VoltaTextSize * sp, TextAnchor.Start));
            }

            if (endsHere)
                items.Add(new LineItem(x2, y, x2, y + hook, thickness));
        }
    }
}
