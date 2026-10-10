using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Layout;

public sealed class LayoutOptions
{
    /// <summary>Distance between two staff lines, in millimetres. Five lines span four staff spaces.</summary>
    public double StaffSpace { get; init; } = 1.75;

    /// <summary>Page margin on every side, in millimetres.</summary>
    public double Margin { get; init; } = 15;

    /// <summary>Width of a measure that has no notes or rests at all, in staff spaces.</summary>
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

    private sealed record MeasureInfo(
        int Number, int Numerator, int Denominator, bool ShowsTimeSignature, int[] Keys, bool KeyChanges,
        bool ClefChangeAtStart, bool SystemBreak, bool PageBreak);

    private sealed record StaffSlot(int PartIndex, int StaffIndex, ClefType Clef, double Offset);

    /// <param name="Prefix">Room at the start of the measure for a key or time signature change, in mm.</param>
    /// <param name="Scale">Factor applied to the note spacing when the system is stretched to the margin.</param>
    private sealed record PlannedMeasure(int Index, double Prefix, double Width, double Scale);

    private sealed record PlannedSystem(int FirstMeasure, double Left, double HeaderWidth, IReadOnlyList<PlannedMeasure> Measures)
    {
        public int MeasureCount => Measures.Count;
    }

    /// <summary>Space between the barline (or the header) and the first note of a measure, in staff spaces.</summary>
    private const double MeasureLeftPadding = 1.2;

    /// <summary>Narrowest measure that holds notes, in staff spaces.</summary>
    private const double MeasureMinContentWidth = 8;

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

        var allSlots = BuildStaffSlots(project, options, isVisible: null);
        if (allSlots.Count == 0)
        {
            page.Items.Add(new TextItem("파트가 없습니다.", pageWidth / 2, y + 10, ComposerSize, TextAnchor.Middle));
            return new ScoreLayout { Pages = pages };
        }

        var clefs = new ClefTimeline(project);
        var measures = BuildMeasureInfos(project);
        var prepared = NotePreparation.Prepare(project, measures.Count, clefs);
        var nameIndent = PartNameIndent(project);
        var planned = PlanSystems(measures, prepared, allSlots, contentLeft, contentRight, nameIndent, options);

        for (var i = 0; i < planned.Count; i++)
        {
            // Staves hidden throughout a system (Finale's hidden empty staves) are left out of it.
            var slots = VisibleSlots(project, planned[i], options) is { Count: > 0 } visible ? visible : allSlots;
            var systemHeight = slots[^1].Offset + 4 * sp;
            var pageBreak = planned[i].MeasureCount > 0 && measures[planned[i].FirstMeasure].PageBreak;
            if ((y + systemHeight > contentBottom || pageBreak) && page.Systems.Count > 0)
            {
                page = NewPage(pages, pageWidth, pageHeight, options);
                y = options.Margin;
            }

            var system = DrawSystem(page, project, planned[i], i == 0, i == planned.Count - 1, slots, clefs, measures, prepared, y, options);
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
            // Composer fields from MusicXML often hold several lines (e.g. lyricist, composer, arranger).
            var lines = project.Composer.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            y += hasTitle ? 6 : ComposerSize;
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) y += ComposerSize * 1.4;
                page.Items.Add(new TextItem(lines[i], page.Width - options.Margin, y, ComposerSize, TextAnchor.End));
            }
        }

        return hasTitle || hasComposer ? y + 12 : y;
    }

    private static List<StaffSlot> BuildStaffSlots(ScoreProject project, LayoutOptions options, Func<int, int, bool>? isVisible)
    {
        var slots = new List<StaffSlot>();
        var offset = 0.0;
        for (var p = 0; p < project.Parts.Count; p++)
        {
            var staves = project.Parts[p].Staves;
            var firstOfPart = true;
            for (var s = 0; s < staves.Count; s++)
            {
                if (isVisible is not null && !isVisible(p, s)) continue;
                if (slots.Count > 0)
                    offset += (4 + (firstOfPart ? options.StaffGapBetweenParts : options.StaffGapInPart)) * options.StaffSpace;
                slots.Add(new StaffSlot(p, s, staves[s].Clef, offset));
                firstOfPart = false;
            }
        }

        return slots;
    }

    /// <summary>Staves that are shown in at least one measure of the system.</summary>
    private static List<StaffSlot> VisibleSlots(ScoreProject project, PlannedSystem system, LayoutOptions options) =>
        BuildStaffSlots(project, options, (p, s) =>
            system.MeasureCount == 0
            || system.Measures.Any(m => project.Parts[p].Measures.Count <= m.Index || !project.Parts[p].Measures[m.Index].HiddenStaves.Contains(s)));

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

            // Keys are per part (transposing instruments differ); a part without this measure keeps its last key.
            var keys = project.Parts.Select((part, p) =>
                part.Measures.Count > i ? part.Measures[i].KeyFifths : previous?.Keys[p] ?? 0).ToArray();
            var keyChanges = previous is not null && !keys.SequenceEqual(previous.Keys);
            var parts = project.Parts.Where(p => p.Measures.Count > i).Select(p => p.Measures[i]).ToList();
            infos.Add(new MeasureInfo(measure.Number, measure.Numerator, measure.Denominator, changed, keys, keyChanges,
                ClefChangeAtStart: i > 0 && parts.Any(m => m.ClefChanges.Any(c => c.PositionNumerator == 0)),
                SystemBreak: i > 0 && parts.Any(m => m.SystemBreak),
                PageBreak: i > 0 && parts.Any(m => m.PageBreak)));
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
        string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Sum(c => c >= '\u1100' ? size : size * 0.55);

    private static double TimeSignatureWidth(MeasureInfo m) =>
        Math.Max(Smufl.TimeSigWidth(m.Numerator), Smufl.TimeSigWidth(m.Denominator));

    /// <summary>Width of a key signature in staff spaces: one column per sharp or flat.</summary>
    private static double KeySignatureWidth(int fifths) => Math.Min(Math.Abs(fifths), 7) * 1.0;

    private static double KeySignatureWidth(MeasureInfo m) => m.Keys.Select(KeySignatureWidth).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Header at the start of a system, in staff spaces: clef, key signature (repeated on every system) and the time
    /// signature when it starts or changes in the system's first measure.
    /// </summary>
    private static double HeaderWidth(MeasureInfo? first, double clefWidth)
    {
        var width = 1 + clefWidth + 1;
        if (first is null) return width;
        var key = KeySignatureWidth(first);
        if (key > 0) width += key + 1;
        if (first.ShowsTimeSignature) width += TimeSignatureWidth(first) + 1.5;
        return width;
    }

    /// <summary>Room at the start of a measure in mid-system for a key or time signature change, in staff spaces.</summary>
    private static double MeasurePrefixWidth(MeasureInfo m)
    {
        var width = m.ClefChangeAtStart ? NotePreparation.ClefChangeWidth : 0.0;
        if (m.KeyChanges) width += KeySignatureWidth(m) + 1;
        if (m.ShowsTimeSignature) width += TimeSignatureWidth(m) + 1;
        return width > 0 ? width + 1 : 0;
    }

    /// <summary>Natural width of a measure's contents in staff spaces, and the part of it that stretches.</summary>
    private static (double Width, double Stretch) MeasureContentWidth(PreparedMeasure measure, LayoutOptions options)
    {
        if (measure.IsEmpty)
            return (options.MeasureMinWidth, options.MeasureMinWidth);

        var segments = measure.Segments.Sum();
        var width = Math.Max(MeasureLeftPadding + measure.Pre.Sum() + segments, MeasureMinContentWidth);
        return (width, segments);
    }

    /// <summary>
    /// How far note spacing may be squeezed to keep the source file's line breaks (e.g. Finale's layout), as a factor
    /// of the natural spacing. Beyond that the measures are spread over more systems.
    /// </summary>
    private const double MinimumSpacingScale = 0.6;

    private static List<PlannedSystem> PlanSystems(
        List<MeasureInfo> measures, PreparedMeasure[] prepared, List<StaffSlot> slots,
        double contentLeft, double contentRight, double nameIndent, LayoutOptions options)
    {
        var sp = options.StaffSpace;
        var clefWidth = slots.Max(s => Smufl.ClefAdvanceWidth(s.Clef));
        var followSourceBreaks = measures.Any(m => m.SystemBreak);
        var systems = new List<PlannedSystem>();
        var next = 0;

        do
        {
            var left = contentLeft + (systems.Count == 0 ? nameIndent : 0);
            var available = contentRight - left;
            var header = HeaderWidth(next < measures.Count ? measures[next] : null, clefWidth) * sp;

            // Measures up to the next source line break (or the end of the piece).
            var segmentEnd = next + 1;
            while (segmentEnd < measures.Count && !measures[segmentEnd].SystemBreak) segmentEnd++;
            if (measures.Count == 0) segmentEnd = 0;

            var segment = new List<(int Index, double Prefix, double Width, double Stretch)>();
            for (var m = next; m < segmentEnd; m++)
            {
                var prefix = m > next ? MeasurePrefixWidth(measures[m]) * sp : 0;
                var (content, stretch) = MeasureContentWidth(prepared[m], options);
                segment.Add((m, prefix, prefix + content * sp, stretch * sp));
            }

            var used = header + segment.Sum(c => c.Width);
            var totalStretch = segment.Sum(c => c.Stretch);
            List<(int Index, double Prefix, double Width, double Stretch)> chosen;
            if (used <= available
                || (followSourceBreaks && totalStretch > 0 && 1 + (available - used) / totalStretch >= MinimumSpacingScale))
            {
                // The whole segment fits, if need be with tighter spacing.
                chosen = segment;
            }
            else
            {
                // Too long: fill the system greedily with as many measures as fit at natural spacing.
                chosen = new List<(int Index, double Prefix, double Width, double Stretch)>();
                var width = header;
                foreach (var candidate in segment)
                {
                    if (chosen.Count > 0 && width + candidate.Width > available) break;
                    chosen.Add(candidate);
                    width += candidate.Width;
                }
            }

            // Stretch (or squeeze) every system but the last to the right margin, in proportion to each measure's
            // note spacing. The last system keeps its natural width unless it has to be squeezed.
            var end = next + chosen.Count;
            used = header + chosen.Sum(c => c.Width);
            totalStretch = chosen.Sum(c => c.Stretch);
            var isLast = end >= measures.Count;
            var extra = totalStretch > 0 && (!isLast || used > available) ? available - used : 0;
            var plannedMeasures = chosen.Select(c =>
            {
                var added = totalStretch > 0 ? extra * c.Stretch / totalStretch : 0;
                var scale = c.Stretch > 0 ? (c.Stretch + added) / c.Stretch : 1;
                return new PlannedMeasure(c.Index, c.Prefix, c.Width + added, scale);
            }).ToList();

            systems.Add(new PlannedSystem(next, left, header, plannedMeasures));
            next = end;
        }
        while (next < measures.Count);

        return systems;
    }

    private static SystemLayout DrawSystem(
        PageLayout page, ScoreProject project, PlannedSystem plan, bool isFirstSystem, bool isLastSystem,
        List<StaffSlot> slots, ClefTimeline clefs, List<MeasureInfo> measures, PreparedMeasure[] prepared, double top, LayoutOptions options)
    {
        var sp = options.StaffSpace;
        var items = page.Items;

        // Each staff starts the system with the clef in force at that point, after any earlier clef changes.
        if (plan.MeasureCount > 0)
            slots = slots.Select(s => s with { Clef = clefs.ClefAt(s.PartIndex, s.StaffIndex, plan.FirstMeasure, Rational.Zero) }).ToList();

        var left = plan.Left;
        var right = plan.MeasureCount == 0
            ? page.Width - options.Margin
            : left + plan.HeaderWidth + plan.Measures.Sum(m => m.Width);
        var staffTops = slots.Select(s => top + s.Offset).ToArray();
        var bottom = staffTops[^1] + 4 * sp;
        var clefWidth = slots.Max(s => Smufl.ClefAdvanceWidth(s.Clef));

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

        // Header: key signature after the clef, then the time signature if it starts here.
        if (plan.MeasureCount > 0)
        {
            var first = measures[plan.FirstMeasure];
            var keyX = left + (1 + clefWidth + 1) * sp;
            DrawKeySignature(items, slots, slots.Select(s => s.Clef).ToArray(), staffTops, first.Keys, keyX, sp);
            if (first.ShowsTimeSignature)
                DrawTimeSignature(items, first, left + plan.HeaderWidth - (TimeSignatureWidth(first) + 1.5) * sp, staffTops, sp);
        }

        // Measures: clef/key/time changes, notes and rests, barlines.
        var measureLayouts = new List<MeasureLayout>();
        var placed = new List<(int Slot, PreparedChord Chord, double X)>();
        var x = left + plan.HeaderWidth;
        for (var k = 0; k < plan.MeasureCount; k++)
        {
            var planned = plan.Measures[k];
            var index = planned.Index;
            var info = measures[index];
            var measureLeft = x;
            var measureRight = x + planned.Width;

            if (k > 0 && planned.Prefix > 0)
            {
                var prefixX = measureLeft + sp;
                if (info.ClefChangeAtStart)
                {
                    for (var i = 0; i < slots.Count; i++)
                    {
                        if (clefs.ChangeAtMeasureStart(slots[i].PartIndex, slots[i].StaffIndex, index) is { } newClef)
                            DrawClef(items, newClef, prefixX, staffTops[i], Smufl.ClefChangeScale, sp);
                    }

                    prefixX += NotePreparation.ClefChangeWidth * sp;
                }

                if (info.KeyChanges)
                {
                    var clefsHere = slots.Select(s => clefs.ClefAt(s.PartIndex, s.StaffIndex, index, Rational.Zero)).ToArray();
                    DrawKeySignature(items, slots, clefsHere, staffTops, info.Keys, prefixX, sp);
                    prefixX += (KeySignatureWidth(info) + 1) * sp;
                }

                if (info.ShowsTimeSignature)
                    DrawTimeSignature(items, info, prefixX, staffTops, sp);
            }

            DrawMeasureNotes(items, prepared[index], slots, staffTops, measureLeft + planned.Prefix, measureRight, planned.Scale, sp, placed);

            var isFinal = isLastSystem && k == plan.MeasureCount - 1;
            DrawBarline(items, slots, top, measureRight, isFinal, sp);
            measureLayouts.Add(new MeasureLayout(index, measureLeft, measureRight, info.ShowsTimeSignature));
            x = measureRight;
        }

        DrawOctaveLines(items, placed, staffTops, sp);

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

    /// <summary>Y of a staff position: 0 is the bottom line, 8 the top line, each step half a staff space.</summary>
    private static double StaffY(double staffTop, int position, double sp) => staffTop + (8 - position) * sp / 2;

    private static void DrawClef(List<DrawItem> items, ClefType clef, double x, double staffTop, double scale, double sp) =>
        items.Add(new GlyphItem(Smufl.ClefGlyph(clef), x, staffTop + Smufl.ClefLine(clef) * sp, 4 * sp * scale));

    private static void DrawKeySignature(
        List<DrawItem> items, List<StaffSlot> slots, ClefType[] clefs, double[] staffTops, int[] keys, double x, double sp)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            var fifths = keys[slots[i].PartIndex];
            var glyph = fifths > 0 ? Smufl.AccidentalSharp : Smufl.AccidentalFlat;
            var positions = Notation.KeySignaturePositions(fifths, clefs[i]);
            for (var a = 0; a < positions.Count; a++)
                items.Add(new GlyphItem(glyph, x + a * sp, StaffY(staffTops[i], positions[a], sp), 4 * sp));
        }
    }

    private static void DrawMeasureNotes(
        List<DrawItem> items, PreparedMeasure measure, List<StaffSlot> slots, double[] staffTops,
        double contentLeft, double measureRight, double scale, double sp, List<(int Slot, PreparedChord Chord, double X)> placed)
    {
        if (measure.IsEmpty) return;

        // X of every onset: padding, then for each onset its accidental room and its (stretched) space to the next.
        var onsetX = new Dictionary<Rational, double>();
        var preStart = new Dictionary<Rational, double>();
        var x = contentLeft + MeasureLeftPadding * sp;
        for (var i = 0; i < measure.Onsets.Count; i++)
        {
            preStart[measure.Onsets[i]] = x;
            x += measure.Pre[i] * sp;
            onsetX[measure.Onsets[i]] = x;
            x += measure.Segments[i] * scale * sp;
        }

        // Mid-measure clef changes sit at the start of the room reserved before their onset.
        foreach (var change in measure.MidMeasureClefs)
        {
            var slotIndex = slots.FindIndex(s => s.PartIndex == change.PartIndex && s.StaffIndex == change.Staff);
            if (slotIndex >= 0 && preStart.TryGetValue(change.Onset, out var clefX))
                DrawClef(items, change.Clef, clefX + 0.2 * sp, staffTops[slotIndex], Smufl.ClefChangeScale, sp);
        }

        foreach (var chord in measure.Chords)
        {
            var slotIndex = slots.FindIndex(s => s.PartIndex == chord.PartIndex && s.StaffIndex == chord.Staff);
            if (slotIndex < 0) continue;
            var staffTop = staffTops[slotIndex];

            if (chord.IsRest)
            {
                var restX = chord.IsMeasureRest
                    ? (contentLeft + measureRight - Smufl.RestWidth(NoteType.Whole) * sp) / 2
                    : onsetX[chord.Start];
                DrawRest(items, chord, restX, staffTop, sp);
            }
            else
            {
                DrawChord(items, chord, onsetX[chord.Start], staffTop, sp);
                placed.Add((slotIndex, chord, onsetX[chord.Start]));
            }
        }
    }

    /// <summary>
    /// Octave lines ("8va - - -┐") over each run of notes that share a shift on a staff: above the staff for 8va/15ma,
    /// below it for 8vb/15mb, clear of the notes and their stems.
    /// </summary>
    private static void DrawOctaveLines(List<DrawItem> items, List<(int Slot, PreparedChord Chord, double X)> placed, double[] staffTops, double sp)
    {
        foreach (var staff in placed.GroupBy(p => p.Slot))
        {
            var ordered = staff.OrderBy(p => p.X).ToList();
            var i = 0;
            while (i < ordered.Count)
            {
                var shift = ordered[i].Chord.OctaveShift;
                var j = i;
                while (j + 1 < ordered.Count && ordered[j + 1].Chord.OctaveShift == shift) j++;
                if (shift != 0)
                    DrawOctaveLine(items, ordered.GetRange(i, j - i + 1), shift, staffTops[staff.Key], sp);
                i = j + 1;
            }
        }
    }

    private static void DrawOctaveLine(List<DrawItem> items, List<(int Slot, PreparedChord Chord, double X)> run, int shift, double staffTop, double sp)
    {
        const double labelScale = 0.8;
        var above = shift > 0;
        var thickness = Smufl.StemThickness * sp;

        double baseline;
        if (above)
        {
            var highest = run.Min(r => StaffY(staffTop, r.Chord.Notes.Max(n => n.Position), sp)
                - (r.Chord.StemUp && r.Chord.Value.Type > NoteType.Whole ? 3.5 : 0.6) * sp);
            baseline = Math.Min(staffTop - 1.5 * sp, highest - 0.8 * sp);
        }
        else
        {
            var lowest = run.Max(r => StaffY(staffTop, r.Chord.Notes.Min(n => n.Position), sp)
                + (!r.Chord.StemUp && r.Chord.Value.Type > NoteType.Whole ? 3.5 : 0.6) * sp);
            baseline = Math.Max(staffTop + 7 * sp, lowest + 2.2 * sp);
        }

        var startX = run[0].X;
        var last = run[^1];
        var endX = last.X + Smufl.NoteheadWidth(last.Chord.Value.Type) * sp;
        items.Add(new GlyphItem(Smufl.OctaveLineLabel(shift), startX, baseline, 4 * sp * labelScale));

        // Dashed line from the label to the last note, then a short hook towards the staff.
        var lineY = baseline - 0.7 * sp;
        for (var dashX = startX + (Smufl.OctaveLineLabelWidth(shift) * labelScale + 0.5) * sp; dashX < endX; dashX += 1.25 * sp)
            items.Add(new LineItem(dashX, lineY, Math.Min(dashX + 0.75 * sp, endX), lineY, thickness));
        items.Add(new LineItem(endX, lineY, endX, lineY + (above ? 1 : -1) * sp, thickness));
    }

    private static void DrawRest(List<DrawItem> items, PreparedChord rest, double x, double staffTop, double sp)
    {
        var type = rest.IsMeasureRest ? NoteType.Whole : rest.Value.Type;

        // Whole rests hang from the fourth line, the others sit on the middle line. With two voices they move apart.
        var y = type <= NoteType.Whole ? staffTop + sp : staffTop + 2 * sp;
        y -= rest.VoiceRole * 2 * sp;
        items.Add(new GlyphItem(Smufl.Rest(type), x, y, 4 * sp));

        if (!rest.IsMeasureRest)
        {
            for (var d = 0; d < rest.Value.Dots; d++)
                items.Add(new GlyphItem(Smufl.AugmentationDot, x + (Smufl.RestWidth(type) + 0.35 + d * 0.6) * sp, staffTop + 1.5 * sp, 4 * sp));
        }
    }

    private static void DrawChord(List<DrawItem> items, PreparedChord chord, double x, double staffTop, double sp)
    {
        var type = chord.Value.Type;
        var headWidth = Smufl.NoteheadWidth(type) * sp;
        var notes = chord.Notes;
        var low = notes.Min(n => n.Position);
        var high = notes.Max(n => n.Position);
        var hasLeft = notes.Any(n => n.Side < 0);
        var hasRight = notes.Any(n => n.Side > 0);

        // Leger lines above and below the staff, wide enough for heads on both sides of the stem.
        var legerLeft = x - (hasLeft ? headWidth : 0) - Smufl.LegerLineExtension * sp;
        var legerRight = x + headWidth + (hasRight ? headWidth : 0) + Smufl.LegerLineExtension * sp;
        for (var p = -2; p >= low; p -= 2)
            items.Add(new LineItem(legerLeft, StaffY(staffTop, p, sp), legerRight, StaffY(staffTop, p, sp), Smufl.LegerLineThickness * sp));
        for (var p = 10; p <= high; p += 2)
            items.Add(new LineItem(legerLeft, StaffY(staffTop, p, sp), legerRight, StaffY(staffTop, p, sp), Smufl.LegerLineThickness * sp));

        var headLeftEdge = x - (hasLeft ? headWidth : 0);
        var dotX = x + headWidth + (hasRight ? headWidth : 0) + 0.35 * sp;
        var dotPositions = new HashSet<int>();
        foreach (var note in notes)
        {
            var y = StaffY(staffTop, note.Position, sp);
            items.Add(new GlyphItem(Smufl.Notehead(type), x + note.Side * headWidth, y, 4 * sp));

            if (note.Accidental is { } alter)
            {
                var accidentalX = headLeftEdge - NotePreparation.AccidentalGap * sp
                    - note.AccidentalColumn * NotePreparation.AccidentalColumnWidth * sp
                    - Smufl.AccidentalWidth(alter) * sp;
                items.Add(new GlyphItem(Smufl.Accidental(alter), accidentalX, y, 4 * sp));
            }

            // Dots sit in a space: a note on a line puts its dot in the space above.
            var dotPosition = note.Position % 2 == 0 ? note.Position + 1 : note.Position;
            if (chord.Value.Dots > 0 && dotPositions.Add(dotPosition))
            {
                for (var d = 0; d < chord.Value.Dots; d++)
                    items.Add(new GlyphItem(Smufl.AugmentationDot, dotX + d * 0.6 * sp, StaffY(staffTop, dotPosition, sp), 4 * sp));
            }
        }

        if (type <= NoteType.Whole)
            return; // whole notes and breves have no stem

        // Stem: from the far note's attachment point to 3.5 spaces past the near note, reaching at least the middle line.
        var thickness = Smufl.StemThickness * sp;
        var extension = type switch { NoteType.ThirtySecond => 0.5, NoteType.SixtyFourth => 1.0, _ => 0 } * sp;
        var middle = staffTop + 2 * sp;
        double stemX, y1, y2;
        if (chord.StemUp)
        {
            stemX = x + headWidth - thickness / 2;
            y1 = StaffY(staffTop, low, sp) - Smufl.StemAttachY * sp;
            y2 = Math.Min(StaffY(staffTop, high, sp) - 3.5 * sp - extension, middle);
        }
        else
        {
            stemX = x + thickness / 2;
            y1 = StaffY(staffTop, high, sp) + Smufl.StemAttachY * sp;
            y2 = Math.Max(StaffY(staffTop, low, sp) + 3.5 * sp + extension, middle);
        }

        items.Add(new LineItem(stemX, y1, stemX, y2, thickness));

        if (Smufl.Flag(type, chord.StemUp) is { } flag)
        {
            // Put the flag's stem anchor on the end of the stem (SMuFL anchors are y-up, the page is y-down).
            var flagY = y2 + Smufl.FlagAnchorY(type, chord.StemUp) * sp;
            items.Add(new GlyphItem(flag, stemX - thickness / 2, flagY, 4 * sp));
        }
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
