using ScoreStudio.Models;

namespace ScoreStudio.Layout;

public sealed class LayoutOptions
{
    /// <summary>Distance between two staff lines, in millimetres. Five lines span four staff spaces.</summary>
    public double StaffSpace { get; init; } = 1.75;

    /// <summary>Page margin on every side, in millimetres.</summary>
    public double Margin { get; init; } = 15;

    /// <summary>Width of a measure before justification, in staff spaces. Content-based spacing comes later.</summary>
    public double MeasureMinWidth { get; init; } = 20;

    /// <summary>Gap between the staves of one part (e.g. a piano grand staff), in staff spaces.</summary>
    public double StaffGapInPart { get; init; } = 6;

    /// <summary>Gap between the last staff of a part and the first staff of the next part, in staff spaces.</summary>
    public double StaffGapBetweenParts { get; init; } = 8;

    /// <summary>Gap between systems, in staff spaces.</summary>
    public double SystemGap { get; init; } = 10;
}

public sealed class ScoreLayout
{
    public required IReadOnlyList<PageLayout> Pages { get; init; }
}

public sealed class PageLayout
{
    public required int Number { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public List<DrawItem> Items { get; } = new();
    public List<SystemLayout> Systems { get; } = new();
}

/// <summary>One line of music across the page: every staff of every part, with a run of measures.</summary>
public sealed class SystemLayout
{
    public required double Top { get; init; }
    public required double Bottom { get; init; }
    public required double Left { get; init; }
    public required double Right { get; init; }
    public required IReadOnlyList<StaffLayout> Staves { get; init; }
    public required IReadOnlyList<MeasureLayout> Measures { get; init; }
}

public sealed record StaffLayout(int PartIndex, int StaffIndex, ClefType Clef, double Top);

public sealed record MeasureLayout(int MeasureIndex, double Left, double Right, bool ShowsTimeSignature);

/// <summary>
/// Turns score data into pages of positioned draw items. Pure computation: no WPF, no files, so it can be tested anywhere.
/// </summary>
public static class ScoreLayoutEngine
{
    private const double TitleSize = 7;
    private const double ComposerSize = 3.5;
    private const double PartNameSize = 3.2;
    private const double MeasureNumberSize = 2.6;
    private const double PageNumberSize = 3.5;

    private sealed record MeasureInfo(int Number, int Numerator, int Denominator, bool ShowsTimeSignature);

    private sealed record StaffSlot(int PartIndex, int StaffIndex, ClefType Clef, double Offset);

    private sealed record PlannedSystem(int FirstMeasure, int MeasureCount, double Left, double HeaderWidth, double[] MeasureWidths);

    public static ScoreLayout Layout(ScoreProject project, LayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new LayoutOptions();
        var sp = options.StaffSpace;
        var (pageWidth, pageHeight) = PageSizes.Get(project.PageSize, project.Orientation);
        var contentLeft = options.Margin;
        var contentRight = pageWidth - options.Margin;
        var contentBottom = pageHeight - options.Margin;

        var pages = new List<PageLayout>();
        var page = NewPage(pages, pageWidth, pageHeight, options);
        var y = DrawTitle(page, project, options);

        var slots = BuildStaffSlots(project, options);
        if (slots.Count == 0)
        {
            page.Items.Add(new TextItem("파트가 없습니다.", pageWidth / 2, y + 10, ComposerSize, TextAnchor.Middle));
            return new ScoreLayout { Pages = pages };
        }

        var systemHeight = slots[^1].Offset + 4 * sp;
        var measures = BuildMeasureInfos(project);
        var nameIndent = PartNameIndent(project);
        var planned = PlanSystems(measures, slots, contentLeft, contentRight, nameIndent, options);

        for (var i = 0; i < planned.Count; i++)
        {
            if (y + systemHeight > contentBottom && page.Systems.Count > 0)
            {
                page = NewPage(pages, pageWidth, pageHeight, options);
                y = options.Margin;
            }

            var system = DrawSystem(page, project, planned[i], i == 0, i == planned.Count - 1, slots, measures, y, options);
            page.Systems.Add(system);
            y += systemHeight + options.SystemGap * sp;
        }

        return new ScoreLayout { Pages = pages };
    }

    private static PageLayout NewPage(List<PageLayout> pages, double width, double height, LayoutOptions options)
    {
        var page = new PageLayout { Number = pages.Count + 1, Width = width, Height = height };
        if (page.Number > 1)
            page.Items.Add(new TextItem(page.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                width - options.Margin, options.Margin / 2 + PageNumberSize / 2, PageNumberSize, TextAnchor.End));
        pages.Add(page);
        return page;
    }

    /// <summary>Draws title and composer on the first page and returns where the first system may start.</summary>
    private static double DrawTitle(PageLayout page, ScoreProject project, LayoutOptions options)
    {
        var y = options.Margin;
        var hasTitle = !string.IsNullOrWhiteSpace(project.Title);
        var hasComposer = !string.IsNullOrWhiteSpace(project.Composer);

        if (hasTitle)
        {
            y += TitleSize;
            page.Items.Add(new TextItem(project.Title.Trim(), page.Width / 2, y, TitleSize, TextAnchor.Middle, Bold: true));
        }

        if (hasComposer)
        {
            y += hasTitle ? 6 : ComposerSize;
            page.Items.Add(new TextItem(project.Composer.Trim(), page.Width - options.Margin, y, ComposerSize, TextAnchor.End));
        }

        return hasTitle || hasComposer ? y + 12 : y;
    }

    private static List<StaffSlot> BuildStaffSlots(ScoreProject project, LayoutOptions options)
    {
        var slots = new List<StaffSlot>();
        var offset = 0.0;
        for (var p = 0; p < project.Parts.Count; p++)
        {
            var staves = project.Parts[p].Staves;
            for (var s = 0; s < staves.Count; s++)
            {
                if (slots.Count > 0)
                    offset += (4 + (s == 0 ? options.StaffGapBetweenParts : options.StaffGapInPart)) * options.StaffSpace;
                slots.Add(new StaffSlot(p, s, staves[s].Clef, offset));
            }
        }

        return slots;
    }

    /// <summary>
    /// Measures are aligned across parts by index. The time signature comes from the first part that has the measure.
    /// </summary>
    private static List<MeasureInfo> BuildMeasureInfos(ScoreProject project)
    {
        var count = project.Parts.Count == 0 ? 0 : project.Parts.Max(p => p.Measures.Count);
        var infos = new List<MeasureInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var measure = project.Parts.First(p => p.Measures.Count > i).Measures[i];
            var previous = i > 0 ? infos[i - 1] : null;
            var changed = previous is null
                || previous.Numerator != measure.Numerator
                || previous.Denominator != measure.Denominator;
            infos.Add(new MeasureInfo(measure.Number, measure.Numerator, measure.Denominator, changed));
        }

        return infos;
    }

    private static double PartNameIndent(ScoreProject project)
    {
        var widest = project.Parts.Select(p => EstimateTextWidth(p.Name, PartNameSize)).DefaultIfEmpty(0).Max();
        return widest <= 0 ? 0 : Math.Min(widest + 5, 40);
    }

    /// <summary>Rough width without font metrics: Hangul and CJK are about one em wide, other characters about half.</summary>
    private static double EstimateTextWidth(string? text, double size) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Sum(c => c >= 'ᄀ' ? size : size * 0.55);

    private static double TimeSignatureWidth(MeasureInfo m) =>
        Math.Max(Smufl.TimeSigWidth(m.Numerator), Smufl.TimeSigWidth(m.Denominator));

    private static List<PlannedSystem> PlanSystems(
        List<MeasureInfo> measures, List<StaffSlot> slots, double contentLeft, double contentRight, double nameIndent, LayoutOptions options)
    {
        var sp = options.StaffSpace;
        var clefWidth = slots.Max(s => Smufl.ClefAdvanceWidth(s.Clef));
        var systems = new List<PlannedSystem>();
        var next = 0;

        do
        {
            var left = contentLeft + (systems.Count == 0 ? nameIndent : 0);
            var available = contentRight - left;
            var header = (1 + clefWidth + 1) * sp;
            if (next < measures.Count && measures[next].ShowsTimeSignature)
                header += (TimeSignatureWidth(measures[next]) + 1.5) * sp;

            var widths = new List<double>();
            var used = header;
            var end = next;
            while (end < measures.Count)
            {
                var width = options.MeasureMinWidth * sp;
                if (end > next && measures[end].ShowsTimeSignature)
                    width += (TimeSignatureWidth(measures[end]) + 1.5) * sp;
                if (end > next && used + width > available)
                    break;
                widths.Add(width);
                used += width;
                end++;
            }

            // Stretch every system but the last to the right margin.
            var isLast = end >= measures.Count;
            if (!isLast && widths.Count > 0)
            {
                var extra = (available - used) / widths.Count;
                for (var w = 0; w < widths.Count; w++) widths[w] += extra;
            }

            systems.Add(new PlannedSystem(next, end - next, left, header, widths.ToArray()));
            next = end;
        }
        while (next < measures.Count);

        return systems;
    }

    private static SystemLayout DrawSystem(
        PageLayout page, ScoreProject project, PlannedSystem plan, bool isFirstSystem, bool isLastSystem,
        List<StaffSlot> slots, List<MeasureInfo> measures, double top, LayoutOptions options)
    {
        var sp = options.StaffSpace;
        var items = page.Items;
        var left = plan.Left;
        var right = plan.MeasureCount == 0
            ? page.Width - options.Margin
            : left + plan.HeaderWidth + plan.MeasureWidths.Sum();
        var staffTops = slots.Select(s => top + s.Offset).ToArray();
        var bottom = staffTops[^1] + 4 * sp;

        // Staff lines and clefs.
        for (var i = 0; i < slots.Count; i++)
        {
            for (var line = 0; line < 5; line++)
            {
                var ly = staffTops[i] + line * sp;
                items.Add(new LineItem(left, ly, right, ly, Smufl.StaffLineThickness * sp));
            }

            items.Add(new GlyphItem(Smufl.ClefGlyph(slots[i].Clef), left + sp, staffTops[i] + Smufl.ClefLine(slots[i].Clef) * sp, 4 * sp));
        }

        // A line joins all staves at the start of the system.
        if (slots.Count > 1)
            items.Add(new LineItem(left, staffTops[0], left, bottom, Smufl.ThinBarlineThickness * sp));

        // Braces for multi-staff parts; part names on the first system.
        foreach (var part in slots.GroupBy(s => s.PartIndex))
        {
            var partTop = top + part.First().Offset;
            var partBottom = top + part.Last().Offset + 4 * sp;
            var braceWidth = 0.0;
            if (part.Count() > 1)
            {
                var size = partBottom - partTop;
                braceWidth = Smufl.BraceAdvanceWidth * size / 4;
                items.Add(new GlyphItem(Smufl.Brace, left - braceWidth - 0.4 * sp, partBottom, size));
            }

            var name = project.Parts[part.Key].Name;
            if (isFirstSystem && !string.IsNullOrWhiteSpace(name))
            {
                var nameX = left - braceWidth - 0.4 * sp - 2;
                items.Add(new TextItem(name.Trim(), nameX, (partTop + partBottom) / 2 + PartNameSize * 0.35, PartNameSize, TextAnchor.End));
            }
        }

        // Measure number at the start of every system but the first.
        if (!isFirstSystem && plan.MeasureCount > 0)
        {
            var number = measures[plan.FirstMeasure].Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            items.Add(new TextItem(number, left, staffTops[0] - 1.5 * sp, MeasureNumberSize, TextAnchor.Start));
        }

        // Measures: time signatures and barlines.
        var measureLayouts = new List<MeasureLayout>();
        var x = left + plan.HeaderWidth;
        if (plan.MeasureCount > 0 && measures[plan.FirstMeasure].ShowsTimeSignature)
            DrawTimeSignature(items, measures[plan.FirstMeasure], x - (TimeSignatureWidth(measures[plan.FirstMeasure]) + 1.5) * sp, staffTops, sp);

        for (var k = 0; k < plan.MeasureCount; k++)
        {
            var index = plan.FirstMeasure + k;
            var measureLeft = x;
            var measureRight = x + plan.MeasureWidths[k];
            if (k > 0 && measures[index].ShowsTimeSignature)
                DrawTimeSignature(items, measures[index], measureLeft + sp, staffTops, sp);

            var isFinal = isLastSystem && k == plan.MeasureCount - 1;
            DrawBarline(items, slots, top, measureRight, isFinal, sp);
            measureLayouts.Add(new MeasureLayout(index, measureLeft, measureRight, measures[index].ShowsTimeSignature));
            x = measureRight;
        }

        return new SystemLayout
        {
            Top = staffTops[0],
            Bottom = bottom,
            Left = left,
            Right = right,
            Staves = slots.Select((s, i) => new StaffLayout(s.PartIndex, s.StaffIndex, s.Clef, staffTops[i])).ToList(),
            Measures = measureLayouts,
        };
    }

    private static void DrawTimeSignature(List<DrawItem> items, MeasureInfo measure, double x, double[] staffTops, double sp)
    {
        var boxWidth = TimeSignatureWidth(measure);
        foreach (var staffTop in staffTops)
        {
            DrawDigits(items, measure.Numerator, x + (boxWidth - Smufl.TimeSigWidth(measure.Numerator)) / 2 * sp, staffTop + sp, sp);
            DrawDigits(items, measure.Denominator, x + (boxWidth - Smufl.TimeSigWidth(measure.Denominator)) / 2 * sp, staffTop + 3 * sp, sp);
        }
    }

    private static void DrawDigits(List<DrawItem> items, int value, double x, double baseline, double sp)
    {
        foreach (var glyph in Smufl.TimeSigDigits(value))
        {
            items.Add(new GlyphItem(glyph, x, baseline, 4 * sp));
            x += Smufl.TimeSigWidth(glyph - Smufl.TimeSig0) * sp;
        }
    }

    /// <summary>Barlines run through all staves of a part but not between parts, so part names and gaps stay readable.</summary>
    private static void DrawBarline(List<DrawItem> items, List<StaffSlot> slots, double top, double x, bool isFinal, double sp)
    {
        var thin = Smufl.ThinBarlineThickness * sp;
        var thick = Smufl.ThickBarlineThickness * sp;
        foreach (var part in slots.GroupBy(s => s.PartIndex))
        {
            var y1 = top + part.First().Offset;
            var y2 = top + part.Last().Offset + 4 * sp;
            if (isFinal)
            {
                items.Add(new LineItem(x - thick / 2, y1, x - thick / 2, y2, thick));
                var thinX = x - thick - Smufl.BarlineSeparation * sp - thin / 2;
                items.Add(new LineItem(thinX, y1, thinX, y2, thin));
            }
            else
            {
                items.Add(new LineItem(x - thin / 2, y1, x - thin / 2, y2, thin));
            }
        }
    }
}
