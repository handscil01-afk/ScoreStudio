using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Layout;

public sealed class LayoutOptions
{
    /// <summary>Distance between two staff lines, in millimetres. Five lines span four staff spaces.</summary>
    public double StaffSpace { get; init; } = 1.75;

    /// <summary>Page margin on every side, in millimetres, unless a side is set on its own below.</summary>
    public double Margin { get; init; } = 15;

    public double? MarginLeft { get; init; }
    public double? MarginRight { get; init; }
    public double? MarginTop { get; init; }
    public double? MarginBottom { get; init; }

    internal double Left => MarginLeft ?? Margin;
    internal double Right => MarginRight ?? Margin;
    internal double Top => MarginTop ?? Margin;
    internal double Bottom => MarginBottom ?? Margin;

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

    /// <summary>Distance between two staff lines the score was laid out with, in mm.</summary>
    public double StaffSpace { get; init; } = 1.75;
}

public sealed class PageLayout
{
    public required int Number { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public List<DrawItem> Items { get; } = new();
    public List<SystemLayout> Systems { get; } = new();

    /// <summary>Where each chord and rest was drawn, so the screen can find what was clicked and mark the selection.</summary>
    public List<ChordBox> Chords { get; } = new();
}

/// <summary>
/// The box around a drawn chord's noteheads (or a rest) in mm, with the chord's place in the score: part, staff, voice,
/// measure and start within the measure (quarter notes).
/// </summary>
public sealed record ChordBox(
    int PartIndex, int Staff, int Voice, int MeasureIndex, Rational Start, bool IsRest, double Left, double Top, double Right, double Bottom)
{
    public bool Contains(double x, double y, double tolerance) =>
        x >= Left - tolerance && x <= Right + tolerance && y >= Top - tolerance && y <= Bottom + tolerance;
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
public static partial class ScoreLayoutEngine
{
    private const double TitleSize = 7;
    private const double ComposerSize = 3.5;
    private const double PartNameSize = 3.2;
    private const double MeasureNumberSize = 2.6;
    private const double PageNumberSize = 3.5;

    private sealed record MeasureInfo(
        int Number, int Numerator, int Denominator, bool ShowsTimeSignature, int[] Keys, bool KeyChanges,
        bool ClefChangeAtStart, bool SystemBreak, bool PageBreak, bool RepeatStart, bool RepeatEnd, ScoreVolta? Volta,
        double? SourceWidth = null);

    /// <summary>Room for a repeat sign (thick line, thin line, dots and a gap), in staff spaces.</summary>
    private const double RepeatSignWidth = 2.2;

    private sealed record StaffSlot(int PartIndex, int StaffIndex, ClefType Clef, double Offset);

    /// <param name="Prefix">Room at the start of the measure for a key or time signature change, in mm.</param>
    /// <param name="Scale">Factor applied to the note spacing when the system is stretched to the margin.</param>
    /// <param name="SlackScale">
    /// Below 1 when the system is squeezed: the part of each gap above its minimum that is kept (0 keeps only the minimum).
    /// </param>
    /// <param name="MinScale">
    /// Below 1 when even the minimum gaps are too wide for the source's measure width: the part of each minimum gap above
    /// the room its notes take that is kept.
    /// </param>
    /// <param name="Compress">
    /// Below 1 when even the narrowest notes overflow the source's system a little: everything in the measure shrinks by it.
    /// </param>
    private sealed record PlannedMeasure(
        int Index, double Prefix, double Width, double Scale, double SlackScale, double MinScale = 1, double Compress = 1);

    /// <summary>
    /// How much a source system may be squeezed beyond its narrowest notes to keep the source's line (fast notes then
    /// come within about a tenth of a space).
    /// </summary>
    private const double MaxCompression = 0.05;

    /// <summary>
    /// A measure as the planner sees it, in mm: natural width (with any key or time change and end repeat), the part
    /// that stretches, how much can be squeezed before gaps reach their minimum, how much more before notes would
    /// touch, its width in the source's layout (staff spaces), and whether it holds lyrics or chord symbols.
    /// </summary>
    private sealed record MeasureCandidate(
        int Index, double Prefix, double Width, double Stretch, double Slack, double Squeeze, double? Source, bool HasText = false)
    {
        public double Narrowest => Width - Slack;
        public double Hardest => Width - Slack - Squeeze;
    }

    private sealed record PlannedSystem(int FirstMeasure, double Left, double HeaderWidth, IReadOnlyList<PlannedMeasure> Measures)
    {
        public int MeasureCount => Measures.Count;
    }

    private const double MeasureLeftPadding = NotePreparation.MeasureLeftPadding;

    /// <summary>Narrowest measure that holds notes, in staff spaces.</summary>
    private const double MeasureMinContentWidth = 8;

    public static ScoreLayout Layout(ScoreProject project, LayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var engraving = project.Engraving;
        options ??= engraving is null
            ? new LayoutOptions()
            : new LayoutOptions
            {
                StaffSpace = engraving.StaffSpace,
                MarginLeft = engraving.MarginLeft, MarginRight = engraving.MarginRight,
                MarginTop = engraving.MarginTop, MarginBottom = engraving.MarginBottom,
            };
        var sp = options.StaffSpace;
        var (pageWidth, pageHeight) = PageSizes.Get(project.PageSize, project.Orientation);
        var contentLeft = options.Left;
        var contentRight = pageWidth - options.Right;
        var contentBottom = pageHeight - options.Bottom;

        var pages = new List<PageLayout>();
        var page = NewPage(pages, pageWidth, pageHeight, options);
        var y = DrawTitle(page, project, options);

        var allSlots = BuildStaffSlots(project, options, isVisible: null);
        if (allSlots.Count == 0)
        {
            page.Items.Add(new TextItem("파트가 없습니다.", pageWidth / 2, y + 10, ComposerSize, TextAnchor.Middle));
            return new ScoreLayout { Pages = pages, StaffSpace = options.StaffSpace };
        }

        var clefs = new ClefTimeline(project);
        var measures = BuildMeasureInfos(project);
        var lyricSize = LyricSize(project, options);
        var prepared = NotePreparation.Prepare(project, measures.Count, clefs, lyricSize / sp);
        var pendingTies = new List<PendingTie>();
        var nameIndent = PartNameIndent(project);
        var planned = PlanSystems(measures, prepared, allSlots, contentLeft, contentRight, nameIndent, options, project.Engraving is not null);

        var titleBottom = y;
        var titleTextBottom = y > options.Top ? y - TitleGap + 2.5 : options.Top;
        var previousBottom = double.NaN;
        var previousLowest = double.NaN;
        for (var i = 0; i < planned.Count; i++)
        {
            // Staves hidden throughout a system (Finale's hidden empty staves) are left out of it.
            var first = planned[i].MeasureCount > 0 ? planned[i].FirstMeasure : -1;
            var slots = VisibleSlots(project, planned[i], options) is { Count: > 0 } visible ? visible : allSlots;
            var systemHeight = slots[^1].Offset + 4 * sp;
            var pageBreak = first >= 0 && measures[first].PageBreak;

            // Draw the system with its top line at 0 first, to see how far chord symbols, brackets and lyrics reach.
            var scratch = new PageLayout { Number = page.Number, Width = pageWidth, Height = pageHeight };
            var drawn = DrawSystem(scratch, project, planned[i], i == 0, i == planned.Count - 1, slots, clefs, measures, prepared, 0, options,
                lyricSize, pendingTies);
            var (above, below) = VerticalExtent(scratch.Items, sp);

            // With the source's layout, systems keep the source's distances; otherwise they follow one after another.
            // Either way a system moves down if it would run into the title (or, without a source layout, into the
            // page margin or the previous system).
            var top = page.Systems.Count == 0 ? FirstSystemTop() : previousBottom + SystemDistance() * sp;
            top = Math.Max(top, Limit() - above - ExtentTolerance * sp);
            if ((top + systemHeight > contentBottom || pageBreak) && page.Systems.Count > 0)
            {
                page = NewPage(pages, pageWidth, pageHeight, options);
                top = Math.Max(FirstSystemTop(), Limit() - above - ExtentTolerance * sp);
            }

            page.Items.AddRange(scratch.Items.Select(item => Shift(item, top)));
            page.Systems.Add(Shift(drawn, top));
            page.Chords.AddRange(scratch.Chords.Select(c => c with { Top = c.Top + top, Bottom = c.Bottom + top }));
            previousBottom = top + systemHeight;
            previousLowest = top + below;

            double FirstSystemTop()
            {
                var below = pages.Count == 1 ? titleBottom : options.Top;
                if (engraving is null) return below;
                var distance = SourceValue(project, first, m => m.TopSystemDistance) ?? engraving.TopSystemDistance;
                return Math.Max(below, options.Top + distance * sp);
            }

            double SystemDistance() =>
                engraving is null ? options.SystemGap : SourceValue(project, first, m => m.SystemDistance) ?? engraving.SystemDistance;

            // The highest a system's items may reach.
            double Limit() =>
                page.Systems.Count == 0 ? (pages.Count == 1 ? titleTextBottom : engraving is null ? options.Top : double.MinValue)
                : engraving is null ? previousLowest + SystemClearance * sp : double.MinValue;
        }

        return new ScoreLayout { Pages = pages, StaffSpace = options.StaffSpace };
    }

    /// <summary>Space between the title block and the first staff, in mm.</summary>
    private const double TitleGap = 12;

    /// <summary>How far a system may reach past its limit before it is moved (a staff line's half thickness), in staff spaces.</summary>
    private const double ExtentTolerance = 0.25;

    /// <summary>Least gap between the lowest item of one system and the highest of the next, in staff spaces.</summary>
    private const double SystemClearance = 1.5;

    /// <summary>
    /// How far drawn items reach above and below y = 0, roughly: text by its font size, glyphs by part of their em,
    /// lines, beams and ties by their geometry.
    /// </summary>
    private static (double Above, double Below) VerticalExtent(IEnumerable<DrawItem> items, double sp)
    {
        var (top, bottom) = (0.0, 0.0);
        foreach (var item in items)
        {
            var (a, b) = item switch
            {
                TextItem t => (t.Y - 0.8 * t.Size, t.Y + 0.25 * t.Size),
                LineItem l => (Math.Min(l.Y1, l.Y2) - l.Thickness / 2, Math.Max(l.Y1, l.Y2) + l.Thickness / 2),
                BeamItem beam => (Math.Min(beam.Y1, beam.Y2), Math.Max(beam.Y1, beam.Y2) + beam.Thickness),
                CurveItem c => (Math.Min(Math.Min(c.Y1, c.Y2), (c.Y1 + 3 * c.ControlY1 + 3 * c.ControlY2 + c.Y2) / 8),
                                Math.Max(Math.Max(c.Y1, c.Y2), (c.Y1 + 3 * c.ControlY1 + 3 * c.ControlY2 + c.Y2) / 8)),
                GlyphItem g => (g.Y - 0.3 * g.Size, g.Y + 0.15 * g.Size),
                _ => (0.0, 0.0),
            };
            (top, bottom) = (Math.Min(top, a), Math.Max(bottom, b));
        }

        return (top, bottom);
    }

    private static DrawItem Shift(DrawItem item, double dy) => item switch
    {
        LineItem l => l with { Y1 = l.Y1 + dy, Y2 = l.Y2 + dy },
        GlyphItem g => g with { Y = g.Y + dy },
        TextItem t => t with { Y = t.Y + dy },
        BeamItem b => b with { Y1 = b.Y1 + dy, Y2 = b.Y2 + dy },
        CurveItem c => c with { Y1 = c.Y1 + dy, Y2 = c.Y2 + dy, ControlY1 = c.ControlY1 + dy, ControlY2 = c.ControlY2 + dy },
        _ => item,
    };

    private static SystemLayout Shift(SystemLayout system, double dy) => new()
    {
        Top = system.Top + dy,
        Bottom = system.Bottom + dy,
        Left = system.Left,
        Right = system.Right,
        Staves = system.Staves.Select(s => s with { Top = s.Top + dy }).ToList(),
        Measures = system.Measures,
    };

    private static PageLayout NewPage(List<PageLayout> pages, double width, double height, LayoutOptions options)
    {
        var page = new PageLayout { Number = pages.Count + 1, Width = width, Height = height };
        if (page.Number > 1)
            page.Items.Add(new TextItem(page.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                width - options.Right, options.Top / 2 + PageNumberSize / 2, PageNumberSize, TextAnchor.End));
        pages.Add(page);
        return page;
    }

    /// <summary>Draws title and composer on the first page and returns where the first system may start.</summary>
    private static double DrawTitle(PageLayout page, ScoreProject project, LayoutOptions options)
    {
        var y = options.Top;
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
                page.Items.Add(new TextItem(lines[i], page.Width - options.Right, y, ComposerSize, TextAnchor.End));
            }
        }

        return hasTitle || hasComposer ? y + TitleGap : y;
    }

    /// <summary>
    /// Staves top to bottom with their offsets from the system's top line. With the source's layout each staff keeps its
    /// distance from the staff above as given for the system starting at <paramref name="systemStart"/>.
    /// </summary>
    private static List<StaffSlot> BuildStaffSlots(ScoreProject project, LayoutOptions options, Func<int, int, bool>? isVisible, int systemStart = -1)
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
                {
                    var gap = project.Engraving is { } engraving
                        ? StaffDistance(project.Parts[p], s, systemStart) ?? engraving.StaffDistance
                        : firstOfPart ? options.StaffGapBetweenParts : options.StaffGapInPart;
                    offset += (4 + gap) * options.StaffSpace;
                }

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
            || system.Measures.Any(m => project.Parts[p].Measures.Count <= m.Index || !project.Parts[p].Measures[m.Index].HiddenStaves.Contains(s)),
            system.MeasureCount == 0 ? -1 : system.FirstMeasure);

    private static double? StaffDistance(ScorePart part, int staff, int measure) =>
        measure >= 0 && measure < part.Measures.Count
            ? part.Measures[measure].StaffDistances.FirstOrDefault(d => d.Staff == staff)?.Distance
            : null;

    /// <summary>A system-wide value from the source at a measure: Finale writes it in the top visible part, so any part may hold it.</summary>
    private static double? SourceValue(ScoreProject project, int measure, Func<ScoreMeasure, double?> value) =>
        measure < 0 ? null : project.Parts.Where(p => measure < p.Measures.Count).Select(p => value(p.Measures[measure])).FirstOrDefault(v => v is not null);

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
                PageBreak: i > 0 && parts.Any(m => m.PageBreak),
                RepeatStart: parts.Any(m => m.RepeatStart),
                RepeatEnd: parts.Any(m => m.RepeatEnd),
                Volta: parts.Select(m => m.Volta).FirstOrDefault(v => v is not null),
                SourceWidth: parts.Select(m => m.SourceWidth).FirstOrDefault(w => w is not null)));
        }

        return infos;
    }

    private static double PartNameIndent(ScoreProject project)
    {
        var widest = project.Parts.Where(p => p.ShowName).Select(p => TextMetrics.Width(p.Name, PartNameSize)).DefaultIfEmpty(0).Max();
        var bracketRoom = project.PartGroups.Any(g => g.Symbol != PartGroupSymbol.None) ? 3 : 0;
        return widest <= 0 ? 0 : Math.Min(widest + 5 + bracketRoom, 43);
    }


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
        if (first.RepeatStart) width += RepeatSignWidth;
        return width;
    }

    /// <summary>Room at the start of a measure in mid-system for a key or time signature change, in staff spaces.</summary>
    private static double MeasurePrefixWidth(MeasureInfo m)
    {
        var width = m.ClefChangeAtStart ? NotePreparation.ClefChangeWidth : 0.0;
        if (m.KeyChanges) width += KeySignatureWidth(m) + 1;
        if (m.ShowsTimeSignature) width += TimeSignatureWidth(m) + 1;
        if (width > 0) width += 1;

        // A start repeat replaces the barline and comes before any clef, key or time change.
        return m.RepeatStart ? width + RepeatSignWidth : width;
    }

    /// <summary>
    /// Natural width of a measure's contents in staff spaces, the part of it that stretches, how much of it can be
    /// squeezed away without notes or syllables running into each other, and how much more before the notes would touch.
    /// </summary>
    private static (double Width, double Stretch, double Slack, double Squeeze) MeasureContentWidth(PreparedMeasure measure, LayoutOptions options)
    {
        // The floor for short measures gives way like the gaps do (Finale sets a lone dotted half in 6/8 under 7 spaces).
        var floor = NotePreparation.MinimumGapScale * MeasureMinContentWidth;
        if (measure.IsEmpty)
        {
            var empty = NotePreparation.MinimumGapScale * options.MeasureMinWidth;
            return (options.MeasureMinWidth, options.MeasureMinWidth, options.MeasureMinWidth - empty, Math.Max(0, empty - floor));
        }

        var segments = measure.Segments.Sum();
        var fixedPart = measure.Lead + measure.Pre.Sum();
        var width = Math.Max(fixedPart + segments, MeasureMinContentWidth);
        var narrowest = Math.Max(fixedPart + measure.MinSegments.Sum(), floor);
        var hardest = Math.Max(measure.HardLead + measure.Pre.Sum() + measure.HardSegments.Sum(), floor);
        return (width, segments, Math.Max(0, width - narrowest), Math.Max(0, narrowest - hardest));
    }

    /// <summary>A measure planned at <paramref name="target"/> mm: stretched, squeezed into its slack, or below its minimum gaps.</summary>
    private static PlannedMeasure FitMeasure(MeasureCandidate c, double target)
    {
        if (target >= c.Width)
            return new PlannedMeasure(c.Index, c.Prefix, target, c.Stretch > 0 ? (c.Stretch + target - c.Width) / c.Stretch : 1, 1);
        if (target >= c.Narrowest)
            return new PlannedMeasure(c.Index, c.Prefix, target, 1, c.Slack > 0 ? 1 - (c.Width - target) / c.Slack : 1);
        var minScale = c.Squeeze > 0 ? Math.Max(0, 1 - (c.Narrowest - target) / c.Squeeze) : 1;
        return new PlannedMeasure(c.Index, c.Prefix, Math.Max(target, c.Hardest), 1, 0, minScale);
    }

    /// <summary>
    /// A source system whose narrowest notes overflow the system by at most <see cref="MaxCompression"/>: every measure at
    /// its narrowest, all shrunk alike to fit. Null when it overflows by more, or when it has lyrics or chord symbols
    /// (shrinking would bring them together).
    /// </summary>
    private static List<PlannedMeasure>? Compressed(List<MeasureCandidate> segment, double header, double available)
    {
        if (segment.Any(c => c.HasText)) return null;
        var hardest = segment.Sum(c => c.Hardest - c.Prefix);
        var fixedWidth = segment.Sum(c => c.Prefix);
        var compress = (available - header - fixedWidth) / hardest;
        if (compress < 1 - MaxCompression) return null;
        return segment.Select(c => new PlannedMeasure(c.Index, c.Prefix, c.Prefix + (c.Hardest - c.Prefix) * compress, 1, 0, 0, compress)).ToList();
    }

    /// <summary>
    /// Plans a source system at the source's measure widths: each measure gets a share of the system in proportion to its
    /// width in the source (the first one less ScoreStudio's header). A measure whose share is narrower than its notes
    /// takes the room its notes need, and the others share what is left. <paramref name="maxScale"/> (mm per source staff
    /// space) keeps a last system that was short in the source short. Null when the notes cannot fit.
    /// </summary>
    private static List<PlannedMeasure>? FitToSourceWidths(List<MeasureCandidate> segment, double header, double available, double? maxScale)
    {
        var pinned = new HashSet<int>();
        while (true)
        {
            var free = Enumerable.Range(0, segment.Count).Where(i => !pinned.Contains(i)).ToList();
            if (free.Count == 0) return Compressed(segment, header, available);

            var room = available - (pinned.Contains(0) ? header : 0) - pinned.Sum(i => segment[i].Hardest);
            var scale = room / free.Sum(i => segment[i].Source!.Value);
            if (maxScale is { } cap) scale = Math.Min(scale, cap);
            if (scale <= 0) return Compressed(segment, header, available);

            double Target(int i) => scale * segment[i].Source!.Value - (i == 0 ? header : 0);
            var tooNarrow = free.Where(i => Target(i) < segment[i].Hardest - 1e-9).ToList();
            if (tooNarrow.Count == 0)
                return segment.Select((c, i) => FitMeasure(c, pinned.Contains(i) ? c.Hardest : Target(i))).ToList();
            pinned.UnionWith(tooNarrow);
        }
    }

    /// <param name="sourceLayout">The score has the source's page layout, so the source's measure widths can be used.</param>
    private static List<PlannedSystem> PlanSystems(
        List<MeasureInfo> measures, PreparedMeasure[] prepared, List<StaffSlot> slots,
        double contentLeft, double contentRight, double nameIndent, LayoutOptions options, bool sourceLayout)
    {
        var sp = options.StaffSpace;
        var clefWidth = slots.Max(s => Smufl.ClefAdvanceWidth(s.Clef));
        var followSourceBreaks = measures.Any(m => m.SystemBreak);
        var systems = new List<PlannedSystem>();
        var next = 0;

        // The source's systems (between its line breaks) and, for a last system that is short in the source, the scale
        // of the others: mm here per staff space of source width.
        var starts = Enumerable.Range(0, measures.Count).Where(m => m == 0 || measures[m].SystemBreak).ToList();
        var fullScales = starts.SkipLast(1).Select((start, s) =>
        {
            var end = s + 1 < starts.Count ? starts[s + 1] : measures.Count;
            var widths = Enumerable.Range(start, end - start).Select(m => measures[m].SourceWidth).ToList();
            return widths.All(w => w is not null) ? (contentRight - contentLeft - (start == 0 ? nameIndent : 0)) / widths.Sum(w => w!.Value) : (double?)null;
        }).Where(s => s is not null).Select(s => s!.Value).ToList();
        double? lastSystemScale = fullScales.Count > 0 ? fullScales.Average() : null;

        do
        {
            var left = contentLeft + (systems.Count == 0 ? nameIndent : 0);
            var available = contentRight - left;
            var header = HeaderWidth(next < measures.Count ? measures[next] : null, clefWidth) * sp;

            // Measures up to the next source line break (or the end of the piece).
            var segmentEnd = next + 1;
            while (segmentEnd < measures.Count && !measures[segmentEnd].SystemBreak) segmentEnd++;
            if (measures.Count == 0) segmentEnd = 0;

            var segment = new List<MeasureCandidate>();
            for (var m = next; m < segmentEnd; m++)
            {
                var prefix = m > next ? MeasurePrefixWidth(measures[m]) * sp : 0;
                var (content, stretch, slack, squeeze) = MeasureContentWidth(prepared[m], options);
                var repeatEnd = measures[m].RepeatEnd ? RepeatSignWidth * sp : 0;
                var hasText = prepared[m].ChordSymbols.Count > 0 || prepared[m].Chords.Any(c => c.Lyrics.Count > 0);
                segment.Add(new MeasureCandidate(m, prefix, prefix + content * sp + repeatEnd, stretch * sp, slack * sp, squeeze * sp, measures[m].SourceWidth, hasText));
            }

            // A source system whose measures all carry their source width is kept whole, at the source's proportions.
            if (sourceLayout && followSourceBreaks && segment.Count > 0 && segment.All(c => c.Source is not null)
                && (next == 0 || measures[next].SystemBreak)
                && FitToSourceWidths(segment, header, available, segmentEnd >= measures.Count && next > 0 ? lastSystemScale : null) is { } fitted)
            {
                systems.Add(new PlannedSystem(next, left, header, fitted));
                next = segmentEnd;
                continue;
            }

            var used = header + segment.Sum(c => c.Width);
            List<MeasureCandidate> chosen;
            if (used <= available || (followSourceBreaks && used - available <= segment.Sum(c => c.Slack)))
            {
                // The whole segment fits, if need be with tighter spacing.
                chosen = segment;
            }
            else
            {
                // Too long: fill the system greedily with as many measures as fit at natural spacing.
                chosen = new List<MeasureCandidate>();
                var width = header;
                foreach (var candidate in segment)
                {
                    if (chosen.Count > 0 && width + candidate.Width > available) break;
                    chosen.Add(candidate);
                    width += candidate.Width;
                }
            }

            // Stretch every system but the last to the right margin, in proportion to each measure's note spacing. A system
            // that is too wide gives up the same share of every gap's slack, so no gap drops below its minimum.
            // The last system keeps its natural width unless it has to be squeezed.
            var end = next + chosen.Count;
            used = header + chosen.Sum(c => c.Width);
            var totalStretch = chosen.Sum(c => c.Stretch);
            var totalSlack = chosen.Sum(c => c.Slack);
            var isLast = end >= measures.Count;
            var extra = totalStretch > 0 && (!isLast || used > available) ? available - used : 0;
            var kept = extra < 0 && totalSlack > 0 ? Math.Max(0, 1 + extra / totalSlack) : 1;
            var plannedMeasures = chosen.Select(c =>
            {
                if (extra < 0)
                    return new PlannedMeasure(c.Index, c.Prefix, c.Width - c.Slack * (1 - kept), 1, kept);

                var added = totalStretch > 0 ? extra * c.Stretch / totalStretch : 0;
                var scale = c.Stretch > 0 ? (c.Stretch + added) / c.Stretch : 1;
                return new PlannedMeasure(c.Index, c.Prefix, c.Width + added, scale, 1);
            }).ToList();

            systems.Add(new PlannedSystem(next, left, header, plannedMeasures));
            next = end;
        }
        while (next < measures.Count);

        return systems;
    }

    private static SystemLayout DrawSystem(
        PageLayout page, ScoreProject project, PlannedSystem plan, bool isFirstSystem, bool isLastSystem,
        List<StaffSlot> slots, ClefTimeline clefs, List<MeasureInfo> measures, PreparedMeasure[] prepared, double top, LayoutOptions options,
        double lyricSize, List<PendingTie> pendingTies)
    {
        var sp = options.StaffSpace;
        var items = page.Items;

        // Each staff starts the system with the clef in force at that point, after any earlier clef changes.
        if (plan.MeasureCount > 0)
            slots = slots.Select(s => s with { Clef = clefs.ClefAt(s.PartIndex, s.StaffIndex, plan.FirstMeasure, Rational.Zero) }).ToList();

        var left = plan.Left;
        var right = plan.MeasureCount == 0
            ? page.Width - options.Right
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

        // Braces for multi-staff parts, then group brackets further out, then part names on the first system.
        var symbolLeft = new Dictionary<int, double>();
        foreach (var part in slots.GroupBy(s => s.PartIndex))
        {
            var partTop = top + part.First().Offset;
            var partBottom = top + part.Last().Offset + 4 * sp;
            symbolLeft[part.Key] = left;
            if (part.Count() > 1)
            {
                var size = partBottom - partTop;
                var braceX = left - Smufl.BraceAdvanceWidth * size / 4 - 0.4 * sp;
                items.Add(new GlyphItem(Smufl.Brace, braceX, partBottom, size));
                symbolLeft[part.Key] = braceX;
            }
        }

        foreach (var group in project.PartGroups.Where(g => g.Symbol != PartGroupSymbol.None))
        {
            var members = slots.Where(s => s.PartIndex >= group.FirstPart && s.PartIndex <= group.LastPart).ToList();
            if (members.Count == 0) continue;
            var groupLeft = DrawGroupSymbol(items, group.Symbol, members.Min(s => symbolLeft[s.PartIndex]),
                top + members[0].Offset, top + members[^1].Offset + 4 * sp, sp);
            foreach (var partIndex in members.Select(s => s.PartIndex).Distinct())
                symbolLeft[partIndex] = Math.Min(symbolLeft[partIndex], groupLeft);
        }

        foreach (var part in slots.GroupBy(s => s.PartIndex))
        {
            var info = project.Parts[part.Key];
            if (!isFirstSystem || !info.ShowName || string.IsNullOrWhiteSpace(info.Name)) continue;
            var partTop = top + part.First().Offset;
            var partBottom = top + part.Last().Offset + 4 * sp;
            items.Add(new TextItem(info.Name.Trim(), symbolLeft[part.Key] - 2, (partTop + partBottom) / 2 + PartNameSize * 0.35, PartNameSize, TextAnchor.End));
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
            // The header ends with the time signature, or with a start repeat after it.
            var headerEnd = left + plan.HeaderWidth - (first.RepeatStart ? RepeatSignWidth * sp : 0);
            if (first.ShowsTimeSignature)
                DrawTimeSignature(items, first, headerEnd - (TimeSignatureWidth(first) + 1.5) * sp, staffTops, sp);
        }

        // Measures: clef/key/time changes, notes and rests, barlines.
        var measureLayouts = new List<MeasureLayout>();
        var placed = new List<PlacedChord>();
        var symbols = new List<PlacedSymbol>();
        var rests = new List<PlacedRest>();
        var x = left + plan.HeaderWidth;
        for (var k = 0; k < plan.MeasureCount; k++)
        {
            var planned = plan.Measures[k];
            var index = planned.Index;
            var info = measures[index];
            var measureLeft = x;
            var measureRight = x + planned.Width;

            // A start repeat at the start of a system follows the clef, key and time signature.
            if (k == 0 && info.RepeatStart)
                DrawBarline(items, project, slots, top, measureLeft - RepeatSignWidth * sp + 0.25 * sp, BarlineKind.StartRepeat, sp);

            if (k > 0 && planned.Prefix > 0)
            {
                var prefixX = measureLeft + (info.RepeatStart ? RepeatSignWidth * sp : 0) + sp;
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

            DrawMeasureNotes(items, prepared[index], slots, staffTops, measureLeft + planned.Prefix, measureRight, planned, sp, placed, index, symbols, rests, page.Chords);

            var kind = isLastSystem && k == plan.MeasureCount - 1 ? BarlineKind.Final : BarlineKind.Normal;
            if (info.RepeatEnd) kind = BarlineKind.EndRepeat;
            if (k + 1 < plan.MeasureCount && measures[plan.Measures[k + 1].Index].RepeatStart) kind |= BarlineKind.StartRepeat;
            DrawBarline(items, project, slots, top, measureRight, kind, sp);
            measureLayouts.Add(new MeasureLayout(index, measureLeft, measureRight, info.ShowsTimeSignature));
            x = measureRight;
        }

        DrawOctaveLines(items, placed, staffTops, sp);
        DrawTies(items, placed, slots, staffTops, left + plan.HeaderWidth, right, pendingTies, sp);
        DrawLyrics(items, placed, rests, staffTops, right, lyricSize, TextMetrics.IsSerifFamily(project.Engraving?.LyricFontFamily), sp);
        var symbolTops = DrawChordSymbols(items, symbols, placed, staffTops, lyricSize, sp);
        DrawVoltas(items, plan, measures, measureLayouts, placed, staffTops[0], symbolTops.TryGetValue(0, out var symbolTop) ? symbolTop : double.MaxValue, sp);

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
        double contentLeft, double measureRight, PlannedMeasure plan, double sp, List<PlacedChord> placed, int measureIndex,
        List<PlacedSymbol> symbols, List<PlacedRest> rests, List<ChordBox> boxes)
    {
        if (measure.IsEmpty)
        {
            PlaceChordSymbols(measure, slots, measureIndex, new List<(Rational, double)>
            {
                (Rational.Zero, contentLeft + MeasureLeftPadding * sp), (measure.Length, measureRight - MeasureLeftPadding * sp),
            }, symbols);
            return;
        }

        // X of every onset: padding, then for each onset its accidental room and its (stretched) space to the next.
        var onsetX = new Dictionary<Rational, double>();
        var preStart = new Dictionary<Rational, double>();
        var unit = plan.Compress * sp;
        var lead = plan.MinScale < 1 ? measure.HardLead + (measure.Lead - measure.HardLead) * plan.MinScale : measure.Lead;
        var x = contentLeft + lead * unit;
        for (var i = 0; i < measure.Onsets.Count; i++)
        {
            preStart[measure.Onsets[i]] = x;
            x += measure.Pre[i] * unit;
            onsetX[measure.Onsets[i]] = x;
            var (hard, min, natural) = (measure.HardSegments[i], measure.MinSegments[i], measure.Segments[i]);
            x += (plan.SlackScale < 1
                ? hard + (min - hard) * plan.MinScale + (natural - min) * plan.SlackScale
                : natural * plan.Scale) * unit;
        }

        // Chord symbols start at their beat: onsets and the measure's end are the anchors between which beats are placed.
        var anchors = measure.Onsets.Select(t => (t, onsetX[t])).ToList();
        if (anchors[0].t.Sign > 0) anchors.Insert(0, (Rational.Zero, contentLeft + lead * unit));
        anchors.Add((measure.Length, x));
        PlaceChordSymbols(measure, slots, measureIndex, anchors, symbols);

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
                var (restTop, restBottom) = DrawRest(items, chord, restX, staffTop, sp);
                rests.Add(new PlacedRest(slotIndex, measureIndex, chord.Start, chord.Voice));
                var restType = chord.IsMeasureRest ? NoteType.Whole : chord.Value.Type;
                boxes.Add(new ChordBox(chord.PartIndex, chord.Staff, chord.Voice, measureIndex, chord.Start, true,
                    restX, restTop, restX + Smufl.RestWidth(restType) * sp, restBottom));
            }
            else
            {
                var chordX = onsetX[chord.Start] + chord.XOffset * sp;
                var (top, bottom) = DrawChord(items, chord, chordX, staffTop, sp);
                placed.Add(new PlacedChord(slotIndex, measureIndex, chord, chordX, top, bottom));
                var head = Smufl.NoteheadWidth(chord.Value.Type) * sp;
                boxes.Add(new ChordBox(chord.PartIndex, chord.Staff, chord.Voice, measureIndex, chord.Start, false,
                    chordX + chord.Notes.Min(n => Math.Min(n.Side, 0)) * head,
                    StaffY(staffTop, chord.Notes.Max(n => n.Position), sp) - sp / 2,
                    chordX + (1 + chord.Notes.Max(n => Math.Max(n.Side, 0))) * head,
                    StaffY(staffTop, chord.Notes.Min(n => n.Position), sp) + sp / 2));
            }
        }

        foreach (var beam in measure.Beams)
        {
            var slotIndex = slots.FindIndex(s => s.PartIndex == beam.Chords[0].PartIndex && s.StaffIndex == beam.Chords[0].Staff);
            if (slotIndex < 0) continue;

            var stemEnds = DrawBeam(items, beam, beam.Chords.Select(c => onsetX[c.Start] + c.XOffset * sp).ToList(), staffTops[slotIndex], sp);
            for (var i = 0; i < beam.Chords.Count; i++)
            {
                var at = placed.FindLastIndex(p => ReferenceEquals(p.Chord, beam.Chords[i]));
                placed[at] = placed[at] with { Top = Math.Min(placed[at].Top, stemEnds[i].Top), Bottom = Math.Max(placed[at].Bottom, stemEnds[i].Bottom) };
            }
        }
    }

    private static void PlaceChordSymbols(
        PreparedMeasure measure, List<StaffSlot> slots, int measureIndex, List<(Rational Time, double X)> anchors, List<PlacedSymbol> symbols)
    {
        foreach (var (partIndex, symbol) in measure.ChordSymbols)
        {
            var slot = slots.FindIndex(s => s.PartIndex == partIndex && s.StaffIndex == symbol.Staff);
            if (slot < 0) continue;
            var x = XAtTime(new Rational(symbol.PositionNumerator, symbol.PositionDenominator), anchors);
            symbols.Add(new PlacedSymbol(slot, measureIndex, x, symbol));
        }
    }

    /// <summary>A drawn chord, the measure it is in, and how far up and down its heads, stem and beams reach, in mm.</summary>
    private sealed record PlacedChord(int Slot, int Measure, PreparedChord Chord, double X, double Top, double Bottom);

    private const double BeamThickness = 0.5;
    private const double BeamSpacing = 0.25;
    private const double BeamHookLength = 1.1;

    /// <summary>
    /// Draws the stems and beams of a beam group. The beam follows the first and last notes, rising at most a quarter
    /// space per staff step and one space overall, and lies flat when an inner note pokes out further than both ends.
    /// It sits so that the shortest stem is 3.5 spaces (more with three or more beams) and every stem reaches the
    /// middle line. Returns how far each chord now reaches up and down.
    /// </summary>
    private static List<(double Top, double Bottom)> DrawBeam(List<DrawItem> items, PreparedBeam beam, List<double> onsetXs, double staffTop, double sp)
    {
        var chords = beam.Chords;
        var up = beam.StemUp;
        var thickness = Smufl.StemThickness * sp;
        var middle = staffTop + 2 * sp;
        var direction = up ? 1 : -1; // from the outer beam towards the notes

        var stemX = new double[chords.Count];
        var nearY = new double[chords.Count];
        var farY = new double[chords.Count];
        var nearPosition = new int[chords.Count];
        for (var i = 0; i < chords.Count; i++)
        {
            var high = chords[i].Notes.Max(n => n.Position);
            var low = chords[i].Notes.Min(n => n.Position);
            var headWidth = Smufl.NoteheadWidth(chords[i].Value.Type) * sp;
            stemX[i] = up ? onsetXs[i] + headWidth - thickness / 2 : onsetXs[i] + thickness / 2;
            nearPosition[i] = up ? high : low;
            nearY[i] = StaffY(staffTop, nearPosition[i], sp);
            farY[i] = up ? StaffY(staffTop, low, sp) - Smufl.StemAttachY * sp : StaffY(staffTop, high, sp) + Smufl.StemAttachY * sp;
        }

        var levels = chords.Max(c => Math.Max(c.Beams.Count, BeamLevels(c)));
        var minimumStem = (3.5 + Math.Max(0, levels - 2) * (BeamThickness + BeamSpacing)) * sp;

        // Slope from the outer notes, unless an inner note sticks out beyond both of them.
        var x0 = stemX[0];
        var width = stemX[^1] - x0;
        var inner = nearY.Skip(1).SkipLast(1).ToList();
        var innerPokesOut = inner.Count > 0 && (up
            ? inner.Min() < Math.Min(nearY[0], nearY[^1])
            : inner.Max() > Math.Max(nearY[0], nearY[^1]));
        var steps = nearPosition[^1] - nearPosition[0];
        var rise = innerPokesOut || width <= 0
            ? 0
            : -Math.Sign(steps) * Math.Min(Math.Min(0.25 * Math.Abs(steps), 1.0), 0.25 * width / sp) * sp;
        var slope = width > 0 ? rise / width : 0;

        // Place the beam where every stem is long enough and reaches the middle line.
        var offset = up ? double.MaxValue : double.MinValue;
        for (var i = 0; i < chords.Count; i++)
        {
            var dx = slope * (stemX[i] - x0);
            offset = up
                ? Math.Min(offset, Math.Min(nearY[i] - minimumStem, middle) - dx)
                : Math.Max(offset, Math.Max(nearY[i] + minimumStem, middle) - dx);
        }

        double BeamY(double x) => offset + slope * (x - x0);

        var reach = new List<(double Top, double Bottom)>();
        for (var i = 0; i < chords.Count; i++)
        {
            var end = BeamY(stemX[i]);
            items.Add(new LineItem(stemX[i], farY[i], stemX[i], end, thickness));
            reach.Add(up ? (end, farY[i]) : (farY[i], end));
        }

        for (var level = 0; level < levels; level++)
        {
            var shift = direction * level * (BeamThickness + BeamSpacing) * sp;
            void Band(double xa, double xb)
            {
                var top = up ? 0 : -BeamThickness * sp;
                items.Add(new BeamItem(xa, BeamY(xa) + shift + top, xb, BeamY(xb) + shift + top, BeamThickness * sp));
            }

            var segments = BeamSegments(chords, level, out var hooks);
            foreach (var (from, to) in segments)
                Band(stemX[from] - thickness / 2, stemX[to] + thickness / 2);
            foreach (var (at, forward) in hooks)
            {
                var neighbour = forward ? (at + 1 < chords.Count ? stemX[at + 1] : stemX[at] + 2 * BeamHookLength * sp)
                                        : (at > 0 ? stemX[at - 1] : stemX[at] - 2 * BeamHookLength * sp);
                var length = Math.Min(BeamHookLength * sp, Math.Abs(neighbour - stemX[at]) / 2);
                if (forward) Band(stemX[at] - thickness / 2, stemX[at] + length);
                else Band(stemX[at] - length, stemX[at] + thickness / 2);
            }
        }

        return reach;
    }

    /// <summary>Beams a note value needs: one for an 8th, two for a 16th, …</summary>
    private static int BeamLevels(PreparedChord chord) => Math.Max(0, chord.Value.Type - NoteType.Quarter);

    /// <summary>
    /// Which chords one beam level joins, and where it is only a hook. Uses the stored beams when they are consistent
    /// for this level; otherwise joins neighbouring notes short enough for the level, and gives a lone note a hook
    /// (pointing left at the end of the group or after a dotted note, right otherwise).
    /// </summary>
    private static List<(int From, int To)> BeamSegments(IReadOnlyList<PreparedChord> chords, int level, out List<(int At, bool Forward)> hooks)
    {
        hooks = new List<(int At, bool Forward)>();
        if (level == 0)
            return new List<(int From, int To)> { (0, chords.Count - 1) };

        var segments = new List<(int From, int To)>();
        var open = -1;
        var consistent = true;
        for (var i = 0; i < chords.Count && consistent; i++)
        {
            BeamType? type = chords[i].Beams.Count > level ? chords[i].Beams[level] : null;
            switch (type)
            {
                case BeamType.Begin:
                    consistent = open < 0;
                    open = i;
                    break;
                case BeamType.Continue:
                    consistent = open >= 0;
                    break;
                case BeamType.End:
                    consistent = open >= 0;
                    segments.Add((open, i));
                    open = -1;
                    break;
                case BeamType.ForwardHook or BeamType.BackwardHook:
                    consistent = open < 0;
                    hooks.Add((i, type == BeamType.ForwardHook));
                    break;
                default:
                    consistent = open < 0 && BeamLevels(chords[i]) <= level;
                    break;
            }
        }

        if (consistent && open < 0)
            return segments;

        // Stored beams do not describe this level: derive it from the note values.
        segments.Clear();
        hooks.Clear();
        for (var i = 0; i < chords.Count;)
        {
            if (BeamLevels(chords[i]) <= level)
            {
                i++;
                continue;
            }

            var j = i;
            while (j + 1 < chords.Count && BeamLevels(chords[j + 1]) > level) j++;
            if (j > i) segments.Add((i, j));
            else hooks.Add((i, !(i == chords.Count - 1 || (i > 0 && chords[i - 1].Value.Dots > 0))));
            i = j + 1;
        }

        return segments;
    }

    /// <summary>
    /// Octave lines ("8va - - -┐") over each run of notes that share a shift on a staff: above the staff for 8va/15ma,
    /// below it for 8vb/15mb, clear of the notes and their stems.
    /// </summary>
    private static void DrawOctaveLines(List<DrawItem> items, List<PlacedChord> placed, double[] staffTops, double sp)
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

    private static void DrawOctaveLine(List<DrawItem> items, List<PlacedChord> run, int shift, double staffTop, double sp)
    {
        const double labelScale = 0.8;
        var above = shift > 0;
        var thickness = Smufl.StemThickness * sp;

        double baseline;
        // Clear of everything drawn for the notes under the line: heads, stems, flags and beams.
        if (above)
            baseline = Math.Min(staffTop - 1.5 * sp, run.Min(r => r.Top) - 0.9 * sp);
        else
            baseline = Math.Max(staffTop + 7 * sp, run.Max(r => r.Bottom) + 2.3 * sp);

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

    /// <summary>Draws a rest (and its dots); returns the top and bottom of the rest glyph.</summary>
    private static (double Top, double Bottom) DrawRest(List<DrawItem> items, PreparedChord rest, double x, double staffTop, double sp)
    {
        var type = rest.IsMeasureRest ? NoteType.Whole : rest.Value.Type;

        // Whole rests hang from the fourth line, the others sit on the middle line. With two voices they move apart.
        var y = type <= NoteType.Whole ? staffTop + sp : staffTop + 2 * sp;
        y -= rest.RestShift * sp / 2;
        items.Add(new GlyphItem(Smufl.Rest(type), x, y, 4 * sp));

        if (!rest.IsMeasureRest)
        {
            for (var d = 0; d < rest.Value.Dots; d++)
                items.Add(new GlyphItem(Smufl.AugmentationDot, x + (Smufl.RestWidth(type) + 0.35 + d * 0.6) * sp, staffTop + 1.5 * sp, 4 * sp));
        }

        var (bottom, top) = Smufl.RestBounds(type);
        return (y - top * sp, y - bottom * sp);
    }

    /// <summary>Draws a chord's heads, accidentals, dots, leger lines and (unless beamed) stem and flag; returns its top and bottom.</summary>
    private static (double Top, double Bottom) DrawChord(List<DrawItem> items, PreparedChord chord, double x, double staffTop, double sp)
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

        var top = StaffY(staffTop, high, sp) - 0.5 * sp;
        var bottom = StaffY(staffTop, low, sp) + 0.5 * sp;
        if (type <= NoteType.Whole || chord.IsBeamed)
            return (top, bottom); // whole notes and breves have no stem; beamed stems come with the beam

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

        return (Math.Min(top, Math.Min(y1, y2)), Math.Max(bottom, Math.Max(y1, y2)));
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

    /// <summary>
    /// Draws a group's bracket, brace, line or square left of <paramref name="right"/> from <paramref name="y1"/> to
    /// <paramref name="y2"/>, and returns its left edge.
    /// </summary>
    private static double DrawGroupSymbol(List<DrawItem> items, PartGroupSymbol symbol, double right, double y1, double y2, double sp)
    {
        var thickness = Smufl.BracketThickness * sp;
        switch (symbol)
        {
            case PartGroupSymbol.Brace:
            {
                var size = y2 - y1;
                var x = right - Smufl.BraceAdvanceWidth * size / 4 - 0.4 * sp;
                items.Add(new GlyphItem(Smufl.Brace, x, y2, size));
                return x;
            }

            case PartGroupSymbol.Bracket:
            {
                // A thick line just outside the staves; the end glyphs curl back over the first and last staff.
                var x = right - 1.0 * sp - thickness;
                items.Add(new LineItem(x + thickness / 2, y1, x + thickness / 2, y2, thickness));
                items.Add(new GlyphItem(Smufl.BracketTop, x, y1, 4 * sp));
                items.Add(new GlyphItem(Smufl.BracketBottom, x, y2, 4 * sp));
                return x;
            }

            case PartGroupSymbol.Square:
            {
                var x = right - 1.0 * sp - thickness;
                var thin = Smufl.ThinBarlineThickness * sp;
                items.Add(new LineItem(x + thickness / 2, y1, x + thickness / 2, y2, thickness));
                items.Add(new LineItem(x, y1 + thin / 2, right - 0.2 * sp, y1 + thin / 2, thin));
                items.Add(new LineItem(x, y2 - thin / 2, right - 0.2 * sp, y2 - thin / 2, thin));
                return x;
            }

            case PartGroupSymbol.Line:
            {
                var x = right - 0.6 * sp;
                items.Add(new LineItem(x, y1, x, y2, Smufl.ThinBarlineThickness * sp));
                return x;
            }

            default:
                return right;
        }
    }

    /// <summary>What a barline looks like.</summary>
    [Flags]
    private enum BarlineKind
    {
        Normal = 0,

        /// <summary>Thin and thick line: the end of the piece.</summary>
        Final = 1,

        /// <summary>Dots, thin and thick line ending at the barline: ":‖".</summary>
        EndRepeat = 2,

        /// <summary>Thick and thin line and dots starting at the barline: "‖:".</summary>
        StartRepeat = 4,
    }

    /// <summary>
    /// Barlines run through all staves of a part, and through all parts of a group that joins its barlines; elsewhere
    /// they stop between parts so part names and lyrics stay readable. Repeat dots go in the two middle spaces of every
    /// staff.
    /// </summary>
    private static void DrawBarline(List<DrawItem> items, ScoreProject project, List<StaffSlot> slots, double top, double x, BarlineKind kind, double sp)
    {
        var thin = Smufl.ThinBarlineThickness * sp;
        var thick = Smufl.ThickBarlineThickness * sp;
        var separation = Smufl.BarlineSeparation * sp;
        var runs = new List<List<StaffSlot>>();
        foreach (var part in slots.GroupBy(s => s.PartIndex))
        {
            var previous = runs.Count > 0 ? runs[^1][^1].PartIndex : -1;
            var joined = previous >= 0 && project.PartGroups.Any(g =>
                g.JoinBarlines && g.FirstPart <= previous && previous <= g.LastPart && g.FirstPart <= part.Key && part.Key <= g.LastPart);
            if (joined) runs[^1].AddRange(part);
            else runs.Add(part.ToList());
        }

        var repeat = kind & (BarlineKind.EndRepeat | BarlineKind.StartRepeat);
        foreach (var run in runs)
        {
            var y1 = top + run[0].Offset;
            var y2 = top + run[^1].Offset + 4 * sp;
            void Line(double centre, double width) => items.Add(new LineItem(centre, y1, centre, y2, width));

            if (repeat == 0)
            {
                if (kind.HasFlag(BarlineKind.Final))
                {
                    Line(x - thick / 2, thick);
                    Line(x - thick - separation - thin / 2, thin);
                }
                else
                {
                    Line(x - thin / 2, thin);
                }

                continue;
            }

            // One thick line on the barline; the thin line and dots on the side (or sides) that repeat.
            Line(kind.HasFlag(BarlineKind.EndRepeat) ? x - thick / 2 : x + thick / 2, thick);
            if (kind.HasFlag(BarlineKind.EndRepeat))
            {
                var thinX = x - thick - separation - thin / 2;
                Line(thinX, thin);
                RepeatDots(items, run, top, thinX - thin / 2 - RepeatDotGap * sp - Smufl.AugmentationDotWidth * sp, sp);
            }

            if (kind.HasFlag(BarlineKind.StartRepeat))
            {
                var thinX = x + thick + separation + thin / 2;
                Line(thinX, thin);
                RepeatDots(items, run, top, thinX + thin / 2 + RepeatDotGap * sp, sp);
            }
        }
    }

    /// <summary>Gap between a repeat's thin line and its dots, in staff spaces.</summary>
    private const double RepeatDotGap = 0.3;

    private static void RepeatDots(List<DrawItem> items, List<StaffSlot> run, double top, double x, double sp)
    {
        foreach (var staff in run)
        {
            items.Add(new GlyphItem(Smufl.AugmentationDot, x, top + staff.Offset + 1.5 * sp, 4 * sp));
            items.Add(new GlyphItem(Smufl.AugmentationDot, x, top + staff.Offset + 2.5 * sp, 4 * sp));
        }
    }
}
