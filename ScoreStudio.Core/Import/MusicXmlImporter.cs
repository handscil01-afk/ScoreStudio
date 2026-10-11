using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio.Import;

/// <summary>
/// Imports partwise MusicXML (.musicxml, .xml) and compressed MusicXML (.mxl) into a <see cref="ScoreProject"/>.
/// Supported elements and the warnings for everything else are listed in docs/SPRINT3_MUSICXML.md.
/// The source file is opened read-only and never written.
/// </summary>
public static class MusicXmlImporter
{
    public const long MaxSourceBytes = 50L * 1024 * 1024;
    public const long MaxXmlCharacters = 200L * 1024 * 1024;

    public static ImportResult ImportFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        XDocument document;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new ProjectFileException($"파일을 찾을 수 없습니다.\n{path}");
            if (info.Length == 0)
                throw new ProjectFileException("파일이 비어 있습니다.");
            if (info.Length > MaxSourceBytes)
                throw new ProjectFileException($"파일이 너무 큽니다({info.Length / 1024 / 1024}MB). {MaxSourceBytes / 1024 / 1024}MB까지 가져올 수 있습니다.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            document = IsZip(stream) ? LoadMxl(stream) : LoadXml(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new ProjectFileException($"파일을 읽을 수 없습니다.\n{path}\n\n{ex.Message}", ex);
        }

        return Convert(document, Path.GetFileNameWithoutExtension(path), path);
    }

    /// <summary>Imports MusicXML text. <paramref name="sourceName"/> is used as the title when the file has none.</summary>
    public static ImportResult ImportXml(string xml, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ProjectFileException("파일이 비어 있습니다.");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return Convert(LoadXml(stream), sourceName, null);
    }

    /// <summary>Imports compressed MusicXML (.mxl) from a stream.</summary>
    public static ImportResult ImportMxl(Stream stream, string sourceName) => Convert(LoadMxl(stream), sourceName, null);

    private static bool IsZip(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        var read = stream.Read(header);
        stream.Position = 0;
        return read == 4 && header[0] == 'P' && header[1] == 'K' && header[2] == 3 && header[3] == 4;
    }

    private static XDocument LoadXml(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            // MusicXML files start with a DOCTYPE that points at the public DTD. Never fetch or expand it.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader);

            // A few exporters add a namespace; MusicXML itself has none, so compare local names only.
            foreach (var element in document.Descendants())
                element.Name = element.Name.LocalName;
            return document;
        }
        catch (XmlException ex)
        {
            throw new ProjectFileException(
                $"MusicXML 파일을 읽을 수 없습니다. XML 형식이 올바르지 않습니다 ({ex.LineNumber}번째 줄 부근).", ex);
        }
    }

    private static XDocument LoadMxl(Stream stream)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var path = RootFilePath(archive)
                ?? archive.Entries.FirstOrDefault(e =>
                    !e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                    && (e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                        || e.FullName.EndsWith(".musicxml", StringComparison.OrdinalIgnoreCase)))?.FullName
                ?? throw new ProjectFileException("압축 MusicXML(.mxl) 파일 안에서 악보 파일을 찾을 수 없습니다.");

            var entry = archive.GetEntry(path)
                ?? throw new ProjectFileException($"압축 MusicXML(.mxl) 파일 안에 '{path}'가 없습니다.");
            if (entry.Length > MaxXmlCharacters)
                throw new ProjectFileException("압축 MusicXML(.mxl) 안의 악보 파일이 너무 큽니다.");

            using var entryStream = entry.Open();
            return LoadXml(entryStream);
        }
        catch (InvalidDataException ex)
        {
            throw new ProjectFileException("압축 MusicXML(.mxl) 파일이 손상되었습니다.", ex);
        }
    }

    private static string? RootFilePath(ZipArchive archive)
    {
        var container = archive.GetEntry("META-INF/container.xml");
        if (container is null) return null;
        using var stream = container.Open();
        return LoadXml(stream).Descendants("rootfile").Select(r => (string?)r.Attribute("full-path")).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    private static ImportResult Convert(XDocument document, string sourceName, string? sourcePath)
    {
        var root = document.Root ?? throw new ProjectFileException("MusicXML 악보 파일이 아닙니다.");
        if (root.Name == "score-timewise")
            throw new ProjectFileException("시간 순서(timewise) 형식의 MusicXML은 아직 지원하지 않습니다. 악보 프로그램에서 일반(partwise) 형식으로 내보내 주세요.");
        if (root.Name != "score-partwise")
            throw new ProjectFileException("MusicXML 악보 파일이 아닙니다. 최상위 요소가 score-partwise가 아닙니다.");

        var warnings = new ImportWarningCollector();
        var project = new ScoreProject
        {
            Title = FirstText(root.Element("work")?.Element("work-title"), root.Element("movement-title")) ?? sourceName,
            Composer = string.Join(", ", root.Element("identification")?.Elements("creator")
                .Where(c => (string?)c.Attribute("type") == "composer")
                .Select(c => c.Value.Trim())
                .Where(v => v.Length > 0) ?? Enumerable.Empty<string>()),
            ImportedFrom = sourcePath ?? sourceName,
            Parts = new List<ScorePart>(),
        };

        var partList = root.Element("part-list")?.Elements("score-part")
            .Where(p => (string?)p.Attribute("id") is not null)
            .ToDictionary(p => (string)p.Attribute("id")!, p => p) ?? new Dictionary<string, XElement>();

        var parts = new List<(XElement Part, string Name, string Instrument)>();
        foreach (var partElement in root.Elements("part"))
        {
            var id = (string?)partElement.Attribute("id") ?? "";
            if (!partList.Remove(id, out var scorePart))
                throw new ProjectFileException($"파트 목록(part-list)에 없는 파트('{id}')가 있습니다. MusicXML 파일이 손상되었을 수 있습니다.");

            var name = PartName(scorePart) ?? $"파트 {parts.Count + 1}";
            var instrument = FirstText(scorePart.Element("score-instrument")?.Element("instrument-name")) ?? name;
            parts.Add((partElement, name, instrument));
        }

        var partIds = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            // Warning locations must say which part they mean, even when several parts share a name.
            var (partElement, name, instrument) = parts[i];
            var label = parts.Count(p => p.Name == name) > 1 ? $"{i + 1}번째 파트({name})" : name;
            var part = new PartReader(name, label, instrument, warnings).Read(partElement);
            var id = (string)partElement.Attribute("id")!;
            part.ShowName = ShowsName(root.Element("part-list")!.Elements("score-part").First(p => (string?)p.Attribute("id") == id));
            project.Parts.Add(part);
            partIds.Add(id);
        }

        project.PartGroups = ReadPartGroups(root.Element("part-list"), partIds, warnings);
        ReadEngraving(root.Element("defaults"), project, warnings);

        foreach (var orphan in partList.Values)
            warnings.Add("파트 목록에만 있고 내용이 없는 파트는 가져오지 않음", FirstText(orphan.Element("part-name")) ?? (string?)orphan.Attribute("id") ?? "");

        if (project.Parts.Count == 0)
            throw new ProjectFileException("MusicXML 파일에 가져올 파트가 없습니다.");

        var events = project.Parts.SelectMany(p => p.Measures).SelectMany(m => m.Events).ToList();
        return new ImportResult
        {
            Project = project,
            Warnings = warnings.ToList(),
            Summary = new ImportSummary
            {
                Parts = project.Parts.Count,
                Measures = project.Parts.Max(p => p.Measures.Count),
                Notes = events.Count(e => e.Kind == "Note"),
                Rests = events.Count(e => e.Kind == "Rest"),
                Lyrics = events.Count(e => e.Lyrics.Count > 0),
                LyricVerses = events.SelectMany(e => e.Lyrics).Select(l => l.Verse).DefaultIfEmpty(0).Max(),
                Ties = events.Count(e => e.TieToNext),
                ChordSymbols = project.Parts.SelectMany(p => p.Measures).Sum(m => m.ChordSymbols.Count),
            },
        };
    }

    /// <summary>Finale names unnamed staves "MusicXML Part"; that is a placeholder, not a name.</summary>
    private const string FinalePlaceholderName = "MusicXML Part";

    /// <summary>
    /// The printed part name: part-name-display if present (it is what the score shows), otherwise part-name.
    /// Returns null for missing or placeholder names.
    /// </summary>
    private static string? PartName(XElement scorePart)
    {
        if (scorePart.Element("part-name-display") is { } display)
        {
            var text = string.Concat(display.Elements().Select(e => e.Name.LocalName switch
            {
                "display-text" => e.Value,
                "accidental-text" => e.Value.Trim() switch { "flat" => "♭", "sharp" => "♯", "natural" => "♮", _ => "" },
                _ => "",
            })).Trim();
            if (text.Length > 0) return text;
        }

        var name = FirstText(scorePart.Element("part-name"));
        return name is null || string.Equals(name, FinalePlaceholderName, StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>A layout distance in tenths of a staff space; null when missing or outside 0–2000 (ten staves or so).</summary>
    private static double? Tenths(XElement? element) =>
        double.TryParse(element?.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 2000
            ? value
            : null;

    /// <summary>
    /// Page size, margins, staff size and default spacing from &lt;defaults&gt;. Without &lt;scaling&gt; the layout keeps
    /// ScoreStudio's defaults. Page sizes other than A4 and Letter are shown on A4.
    /// </summary>
    private static void ReadEngraving(XElement? defaults, ScoreProject project, ImportWarningCollector warnings)
    {
        var scaling = defaults?.Element("scaling");
        if (!double.TryParse(scaling?.Element("millimeters")?.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var millimeters)
            || !double.TryParse(scaling?.Element("tenths")?.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tenths)
            || millimeters <= 0 || tenths <= 0)
            return;

        var mmPerTenth = millimeters / tenths;
        var engraving = new ScoreEngraving { StaffSpace = mmPerTenth * 10 };
        if (engraving.StaffSpace is < 0.5 or > 5)
        {
            warnings.Add("원본의 보표 크기를 쓸 수 없어 기본 배치로 표시함", "악보 설정");
            return;
        }

        var page = defaults!.Element("page-layout");
        if (Tenths(page?.Element("page-width")) is { } width && Tenths(page?.Element("page-height")) is { } height)
        {
            var (w, h) = (width * mmPerTenth, height * mmPerTenth);
            var (shortSide, longSide) = (Math.Min(w, h), Math.Max(w, h));
            project.Orientation = w > h ? "Landscape" : "Portrait";
            if (Math.Abs(shortSide - 215.9) < 3 && Math.Abs(longSide - 279.4) < 3) project.PageSize = "Letter";
            else
            {
                project.PageSize = "A4";
                if (Math.Abs(shortSide - 210) >= 3 || Math.Abs(longSide - 297) >= 3)
                    warnings.Add($"지원하지 않는 용지 크기({w:0}×{h:0}mm)는 A4로 표시함", "악보 설정");
            }
        }

        var margins = page?.Elements("page-margins").OrderBy(m => (string?)m.Attribute("type") switch { "both" => 0, "odd" => 1, _ => 2 }).FirstOrDefault();
        double Margin(string name, double fallback) =>
            Tenths(margins?.Element(name)) is { } t && t * mmPerTenth <= 100 ? t * mmPerTenth : fallback;
        engraving.MarginLeft = Margin("left-margin", engraving.MarginLeft);
        engraving.MarginRight = Margin("right-margin", engraving.MarginRight);
        engraving.MarginTop = Margin("top-margin", engraving.MarginTop);
        engraving.MarginBottom = Margin("bottom-margin", engraving.MarginBottom);

        var system = defaults.Element("system-layout");
        if (Tenths(system?.Element("system-distance")) is { } systemDistance) engraving.SystemDistance = systemDistance / 10;
        if (Tenths(system?.Element("top-system-distance")) is { } topDistance) engraving.TopSystemDistance = topDistance / 10;
        var staffLayout = defaults.Elements("staff-layout").FirstOrDefault(s => (string?)s.Attribute("number") is null or "1");
        if (Tenths(staffLayout?.Element("staff-distance")) is { } staffDistance) engraving.StaffDistance = staffDistance / 10;

        // Font sizes are in points.
        if (double.TryParse((string?)defaults.Element("lyric-font")?.Attribute("font-size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var points)
            && points is >= 3 and <= 56)
            engraving.LyricFontSize = points * 25.4 / 72;

        project.Engraving = engraving;
    }

    /// <summary>Finale hides part names with print-object="no" on part-name (or on part-name-display).</summary>
    private static bool ShowsName(XElement scorePart) =>
        (string?)scorePart.Element("part-name")?.Attribute("print-object") != "no"
        && (string?)scorePart.Element("part-name-display")?.Attribute("print-object") != "no";

    /// <summary>
    /// &lt;part-group type="start"/"stop"&gt; around score-parts in the part list. Groups are matched by number and may
    /// nest. A group left open runs to the last part; a group around no imported part is dropped.
    /// </summary>
    private static List<ScorePartGroup> ReadPartGroups(XElement? partList, List<string> partIds, ImportWarningCollector warnings)
    {
        var groups = new List<ScorePartGroup>();
        if (partList is null) return groups;

        var open = new Dictionary<string, (XElement Start, int FirstPart)>();
        var nextPart = 0;
        foreach (var element in partList.Elements())
        {
            if (element.Name == "score-part")
            {
                if (partIds.IndexOf((string?)element.Attribute("id") ?? "") is >= 0 and var index) nextPart = index + 1;
                continue;
            }

            if (element.Name != "part-group") continue;
            var number = (string?)element.Attribute("number") ?? "1";
            var type = (string?)element.Attribute("type");
            if (type == "start")
                open[number] = (element, nextPart);
            else if (type == "stop" && open.Remove(number, out var started))
                AddGroup(started.Start, started.FirstPart, nextPart - 1);
        }

        foreach (var (start, firstPart) in open.Values)
        {
            warnings.Add("끝나지 않은 파트 묶음은 마지막 파트까지로 맞춤", "파트 목록");
            AddGroup(start, firstPart, partIds.Count - 1);
        }

        return groups;

        void AddGroup(XElement start, int first, int last)
        {
            if (first > last || first >= partIds.Count) return;
            var symbol = start.Element("group-symbol")?.Value.Trim() switch
            {
                "brace" => PartGroupSymbol.Brace,
                "bracket" => PartGroupSymbol.Bracket,
                "line" => PartGroupSymbol.Line,
                "square" => PartGroupSymbol.Square,
                _ => PartGroupSymbol.None,
            };
            groups.Add(new ScorePartGroup
            {
                FirstPart = first, LastPart = last, Symbol = symbol,
                JoinBarlines = start.Element("group-barline")?.Value.Trim() == "yes",
            });
        }
    }

    private static string? FirstText(params XElement?[] elements) =>
        elements.Select(e => e?.Value.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>Reads one &lt;part&gt;. Attributes such as divisions and time signature carry over between measures.</summary>
    private sealed class PartReader
    {
        private static readonly Dictionary<string, string> NotationNames = new()
        {
            ["slur"] = "이음줄",
            ["tuplet"] = "잇단음표 괄호·숫자 표시 (음 길이는 정확히 가져옴)",
            ["articulations"] = "아티큘레이션(스타카토, 악센트 등)",
            ["ornaments"] = "장식음 기호(트릴, 턴 등)",
            ["fermata"] = "페르마타",
            ["dynamics"] = "셈여림",
            ["technical"] = "주법 기호",
            ["arpeggiate"] = "아르페지오",
            ["non-arpeggiate"] = "아르페지오",
            ["glissando"] = "글리산도",
            ["slide"] = "글리산도",
            ["accidental-mark"] = "임시표 기호",
        };

        private static readonly Dictionary<string, string> DirectionNames = new()
        {
            ["dynamics"] = "셈여림",
            ["words"] = "글자 지시(빠르기말, 표정 등)",
            ["metronome"] = "메트로놈 빠르기 표시",
            ["wedge"] = "크레셴도·디크레셴도",
            ["pedal"] = "페달",
            ["rehearsal"] = "리허설 마크",
            ["segno"] = "세뇨·코다",
            ["coda"] = "세뇨·코다",
            ["dashes"] = "점선 지시(cresc. - - - 등)",
            ["bracket"] = "괄호선 지시",
            ["other-direction"] = "기타 지시 기호",
        };

        private static readonly HashSet<string> IgnoredMeasureChildren = new() { "sound", "listening", "bookmark", "link", "grouping" };

        private readonly string _name;
        private readonly string _label;
        private readonly string _instrument;
        private readonly ImportWarningCollector _warnings;
        private readonly Dictionary<int, ClefType> _clefs = new();
        private readonly Dictionary<int, ClefType> _currentClefs = new();
        private readonly HashSet<int> _hiddenStaves = new();

        /// <summary>Octave-line starts and stops per staff, applied to notes by time once the part is read.</summary>
        private readonly List<(int Staff, int Measure, Rational Time, int Shift)> _octaveChanges = new();

        private long _divisions;
        private int _numerator = 4;
        private int _denominator = 4;
        private int _keyFifths;
        private int _staves = 1;
        private bool _firstMeasureDone;
        private string _location = "";
        private ScoreVolta? _openVolta;
        private int _openVoltaStart;

        public PartReader(string name, string label, string instrument, ImportWarningCollector warnings)
        {
            _name = name;
            _label = label;
            _instrument = instrument;
            _warnings = warnings;
        }

        public ScorePart Read(XElement part)
        {
            var measures = new List<ScoreMeasure>();
            foreach (var measureElement in part.Elements("measure"))
            {
                measures.Add(ReadMeasure(measureElement, measures.Count));
                _firstMeasureDone = true;
            }

            if (_openVolta is not null)
            {
                Warn("끝나지 않은 볼타(1·2번 괄호)는 마지막 마디까지로 맞춤");
                _openVolta.MeasureCount = measures.Count - _openVoltaStart;
                _openVolta.OpenEnd = true;
            }

            ApplyOctaveShifts(measures);
            foreach (var measure in measures)
            {
                measure.HiddenStaves.RemoveAll(s => s >= _staves);
                measure.StaffDistances.RemoveAll(d => d.Staff >= _staves);
            }

            var staves = Enumerable.Range(1, _staves)
                .Select(n => new ScoreStaff { Clef = _clefs.TryGetValue(n, out var clef) ? clef : n == 2 ? ClefType.Bass : ClefType.Treble })
                .ToList();

            // Events on staves that were declared later than they were used are clamped in ReadNote.
            return new ScorePart { Name = _name, Instrument = _instrument, Staves = staves, Measures = measures };
        }

        private void Warn(string message) => _warnings.Add(message, _location);

        private ScoreMeasure ReadMeasure(XElement element, int index)
        {
            var numberText = (string?)element.Attribute("number");
            var isNumeric = int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed);
            var number = isNumeric ? parsed : index + 1;
            _location = $"{_label} {numberText ?? number.ToString(CultureInfo.InvariantCulture)}마디";
            if (!isNumeric)
                Warn("숫자가 아닌 마디 번호는 순서 번호로 바꿈");

            var measure = new ScoreMeasure { Number = number, Numerator = _numerator, Denominator = _denominator, KeyFifths = _keyFifths };
            var cursor = Rational.Zero;
            var lastStart = Rational.Zero;
            var end = Rational.Zero;

            foreach (var child in element.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "attributes":
                        ReadAttributes(child, measure, cursor);
                        break;
                    case "print":
                        // Line and page breaks chosen in the source program (e.g. Finale's layout).
                        if (index > 0 && ((string?)child.Attribute("new-system") == "yes" || (string?)child.Attribute("new-page") == "yes"))
                            measure.SystemBreak = true;
                        if (index > 0 && (string?)child.Attribute("new-page") == "yes")
                            measure.PageBreak = true;
                        ReadPrintLayout(child, measure);
                        break;
                    case "note":
                        ReadNote(child, measure, ref cursor, ref lastStart);
                        if (cursor > end) end = cursor;
                        break;
                    case "backup":
                        cursor -= Duration(child);
                        if (cursor.Sign < 0)
                        {
                            Warn("마디 시작보다 앞으로 되돌아가는 위치(backup)를 마디 시작으로 맞춤");
                            cursor = Rational.Zero;
                        }
                        break;
                    case "forward":
                        cursor += Duration(child);
                        if (cursor > end) end = cursor;
                        break;
                    case "direction":
                        foreach (var shift in child.Elements("direction-type").SelectMany(d => d.Elements("octave-shift")))
                            ReadOctaveShift(shift, child, index, cursor);
                        foreach (var type in child.Elements("direction-type").SelectMany(d => d.Elements())
                                     .Where(e => e.Name != "octave-shift").Where(IsStartOrSingle))
                            Warn(DirectionNames.TryGetValue(type.Name.LocalName, out var label) ? label : $"지시 기호 <{type.Name.LocalName}>");
                        break;
                    case "barline":
                        ReadBarline(child, measure, index);
                        break;
                    case "harmony":
                        ReadHarmony(child, measure, cursor);
                        break;
                    case "figured-bass":
                        Warn("숫자 저음");
                        break;
                    default:
                        if (!IgnoredMeasureChildren.Contains(child.Name.LocalName))
                            Warn($"알 수 없는 요소 <{child.Name.LocalName}>");
                        break;
                }
            }

            if (end > new Rational(4L * measure.Numerator, measure.Denominator))
                Warn("박자표보다 긴 마디가 있음 (그대로 가져옴)");

            measure.HiddenStaves.AddRange(_hiddenStaves.OrderBy(s => s));
            return measure;
        }

        private void ReadAttributes(XElement attributes, ScoreMeasure measure, Rational cursor)
        {
            if (attributes.Element("divisions") is { } divisionsElement)
            {
                if (long.TryParse(divisionsElement.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var divisions) && divisions > 0)
                    _divisions = divisions;
                else
                    Warn("올바르지 않은 divisions 값은 무시함");
            }

            foreach (var key in attributes.Elements("key"))
            {
                if (key.Element("fifths") is { } fifthsElement
                    && int.TryParse(fifthsElement.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fifths)
                    && fifths is >= -7 and <= 7)
                {
                    _keyFifths = fifths;
                    measure.KeyFifths = fifths;
                }
                else
                {
                    Warn("일반적이지 않은 조표는 가져오지 않음");
                }
            }

            foreach (var time in attributes.Elements("time"))
                ReadTime(time, measure);

            if (attributes.Element("staves") is { } stavesElement
                && int.TryParse(stavesElement.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var staves) && staves > 0)
            {
                if (_firstMeasureDone && staves != _staves)
                    Warn("중간에 바뀌는 보표 수 (가장 많은 수로 맞춤)");
                _staves = Math.Max(_staves, Math.Min(staves, 4));
                if (staves > 4) Warn("보표가 5개 이상인 파트는 4개까지만 가져옴");
            }

            foreach (var clef in attributes.Elements("clef"))
                ReadClef(clef, measure, cursor);

            // Hidden staves (Finale's "hide empty staves"): print-object="no" hides from here on, "yes" shows again.
            foreach (var details in attributes.Elements("staff-details"))
            {
                var printObject = (string?)details.Attribute("print-object");
                if (printObject is not ("yes" or "no")) continue;
                var targets = int.TryParse((string?)details.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    ? new[] { number - 1 }
                    : Enumerable.Range(0, 4).ToArray();
                foreach (var staff in targets.Where(t => t >= 0))
                {
                    if (printObject == "no") _hiddenStaves.Add(staff);
                    else _hiddenStaves.Remove(staff);
                }
            }

            if (attributes.Element("transpose") is not null)
                Warn("이조 악기 설정(transpose)은 가져오지 않음 (기보된 음높이로 가져옴)");
        }

        private void ReadTime(XElement time, ScoreMeasure measure)
        {
            if (time.Element("senza-misura") is not null)
            {
                Warn("박자 없음(senza misura)은 가져오지 않음");
                return;
            }

            var beats = time.Elements("beats").Select(b => b.Value.Trim()).ToList();
            var beatTypes = time.Elements("beat-type").Select(b => b.Value.Trim()).ToList();
            if (beats.Count == 0 || beatTypes.Count == 0)
                return;

            if (beats.Count > 1 || beats[0].Contains('+'))
                Warn("복합 박자 표기(3+2 등)는 합한 박자로 가져옴");

            int numerator;
            int denominator;
            try
            {
                numerator = beats[0].Split('+').Sum(b => int.Parse(b, NumberStyles.Integer, CultureInfo.InvariantCulture));
                denominator = int.Parse(beatTypes[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
                for (var i = 1; i < beats.Count && i < beatTypes.Count; i++)
                {
                    // Add a/b to the running n/d: everything is expressed over the larger denominator.
                    var addNumerator = beats[i].Split('+').Sum(b => int.Parse(b, NumberStyles.Integer, CultureInfo.InvariantCulture));
                    var addDenominator = int.Parse(beatTypes[i], NumberStyles.Integer, CultureInfo.InvariantCulture);
                    var common = Math.Max(denominator, addDenominator);
                    numerator = numerator * (common / denominator) + addNumerator * (common / addDenominator);
                    denominator = common;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or DivideByZeroException)
            {
                Warn("읽을 수 없는 박자표는 이전 박자를 유지함");
                return;
            }

            if (numerator <= 0 || denominator <= 0 || numerator > 99 || denominator > 64)
            {
                Warn("읽을 수 없는 박자표는 이전 박자를 유지함");
                return;
            }

            _numerator = measure.Numerator = numerator;
            _denominator = measure.Denominator = denominator;
        }

        private void ReadClef(XElement clef, ScoreMeasure measure, Rational cursor)
        {
            var staff = 1;
            if (clef.Attribute("number") is { } numberAttribute
                && !int.TryParse(numberAttribute.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out staff))
                staff = 0;
            if (staff < 1 || staff > 4)
            {
                Warn("읽을 수 없는 보표 번호의 음자리표는 무시함");
                return;
            }

            var sign = clef.Element("sign")?.Value.Trim().ToUpperInvariant();
            var line = int.TryParse(clef.Element("line")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : (int?)null;
            var octave = int.TryParse(clef.Element("clef-octave-change")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0;

            ClefType? type = (sign, line ?? DefaultLine(sign), octave) switch
            {
                ("G", 2, 0) => ClefType.Treble,
                ("G", 2, -1) => ClefType.Treble8vb,
                ("F", 4, 0) => ClefType.Bass,
                ("C", 3, 0) => ClefType.Alto,
                ("C", 4, 0) => ClefType.Tenor,
                _ => null,
            };

            if (type is null)
            {
                Warn($"지원하지 않는 음자리표({sign ?? "?"}{(line is null ? "" : $", {line}번 줄")}{(octave == 0 ? "" : $", 옥타브 {octave:+0;-0}")})는 비슷한 음자리표로 바꿈");
                type = sign switch { "F" => ClefType.Bass, "C" => ClefType.Alto, _ => ClefType.Treble };
            }

            if (!_clefs.ContainsKey(staff))
            {
                // The first clef of a staff is its starting clef, wherever it appears.
                _clefs[staff] = type.Value;
                _currentClefs[staff] = type.Value;
                _staves = Math.Max(_staves, staff);
                return;
            }

            if (_currentClefs[staff] == type.Value)
                return;

            _currentClefs[staff] = type.Value;
            var change = new ScoreClefChange { Staff = staff - 1, Clef = type.Value };
            if (TrySetFraction(cursor, (n, d) => (change.PositionNumerator, change.PositionDenominator) = (n, d)))
                measure.ClefChanges.Add(change);
        }

        /// <summary>
        /// Spacing the source chose for the system (or page) that starts here, in tenths of a staff space: the distance
        /// from the previous system or the top margin, and each staff's distance from the staff above. Staff numbers
        /// beyond the part's staves are dropped once the part is read (a print may come before &lt;staves&gt;).
        /// </summary>
        private static void ReadPrintLayout(XElement print, ScoreMeasure measure)
        {
            var system = print.Element("system-layout");
            if (Tenths(system?.Element("system-distance")) is { } distance) measure.SystemDistance = distance / 10;
            if (Tenths(system?.Element("top-system-distance")) is { } top) measure.TopSystemDistance = top / 10;

            foreach (var staffLayout in print.Elements("staff-layout"))
            {
                var number = int.TryParse((string?)staffLayout.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
                if (number >= 1 && Tenths(staffLayout.Element("staff-distance")) is { } staffDistance)
                {
                    measure.StaffDistances.RemoveAll(d => d.Staff == number - 1);
                    measure.StaffDistances.Add(new ScoreStaffDistance { Staff = number - 1, Distance = staffDistance / 10 });
                }
            }
        }

        /// <summary>
        /// An 8va line (type "down": notes are written an octave lower than they sound), 8vb ("up"), 15ma/15mb (size 15),
        /// or the end of one ("stop"). It applies by time to every voice on its staff. Finale writes the stop just before
        /// the last note under the line, so notes starting at the stop's time are still shifted.
        /// </summary>
        private void ReadOctaveShift(XElement shift, XElement direction, int measureIndex, Rational cursor)
        {
            var type = (string?)shift.Attribute("type");
            if (type == "continue") return;

            var staff = int.TryParse(direction.Element("staff")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n - 1 : 0;
            var size = int.TryParse((string?)shift.Attribute("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 8;
            var octaves = size >= 15 ? 2 : 1;
            if (size > 15) Warn("22ma 이상의 옥타브 기호는 15ma로 가져옴");

            var value = type switch { "down" => octaves, "up" => -octaves, _ => 0 };
            _octaveChanges.Add((staff, measureIndex, cursor, value));
        }

        private void ApplyOctaveShifts(List<ScoreMeasure> measures)
        {
            if (_octaveChanges.Count == 0) return;

            // Directions after a <backup> lie earlier in time than their place in the file, so order by time.
            var changes = _octaveChanges.Select((c, i) => (Change: c, Order: i))
                .OrderBy(x => x.Change.Measure).ThenBy(x => x.Change.Time).ThenBy(x => x.Order)
                .Select(x => x.Change).ToList();

            for (var m = 0; m < measures.Count; m++)
            {
                foreach (var item in measures[m].Events)
                {
                    var start = new Rational(item.PositionNumerator, item.PositionDenominator);
                    var shift = 0;
                    foreach (var change in changes)
                    {
                        if (change.Staff != item.Staff) continue;
                        var isStop = change.Shift == 0;
                        var comesLater = change.Measure > m
                            || (change.Measure == m && (isStop ? change.Time >= start : change.Time > start));
                        if (!comesLater) shift = change.Shift;
                    }

                    item.OctaveShift = shift;
                }
            }
        }

        private static int? DefaultLine(string? sign) => sign switch { "G" => 2, "F" => 4, "C" => 3, _ => null };

        /// <summary>Duration of a note, backup or forward in quarter notes.</summary>
        private Rational Duration(XElement element)
        {
            if (!long.TryParse(element.Element("duration")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) || duration < 0)
                return Rational.Zero;
            if (_divisions <= 0)
            {
                Warn("divisions가 없어 4분음표 1개를 1로 가정함");
                _divisions = 1;
            }

            return new Rational(duration, _divisions);
        }

        private void ReadNote(XElement note, ScoreMeasure measure, ref Rational cursor, ref Rational lastStart)
        {
            if (note.Element("grace") is not null)
            {
                Warn("꾸밈음");
                return;
            }

            var isChord = note.Element("chord") is not null;
            var duration = Duration(note);
            if (duration.Sign <= 0)
            {
                Warn("길이가 없는 음표는 가져오지 않음");
                return;
            }

            var start = isChord ? lastStart : cursor;
            if (!isChord)
            {
                lastStart = cursor;
                cursor += duration;
            }

            if (note.Element("cue") is not null)
            {
                Warn("큐 음표");
                return;
            }

            if (note.Element("unpitched") is not null)
            {
                Warn("타악기 음표(unpitched)");
                return;
            }

            var item = new ScoreEvent { Staff = ReadStaff(note), Voice = ReadVoice(note) };
            if (note.Element("rest") is not null)
            {
                item.Kind = "Rest";
            }
            else if (note.Element("pitch") is { } pitch && ReadPitch(pitch, item))
            {
                item.Kind = "Note";
            }
            else
            {
                Warn("음높이를 읽을 수 없는 음표는 가져오지 않음");
                return;
            }

            if (!TrySetFraction(duration, (n, d) => (item.DurationNumerator, item.DurationDenominator) = (n, d))
                || !TrySetFraction(start, (n, d) => (item.PositionNumerator, item.PositionDenominator) = (n, d)))
            {
                Warn("너무 복잡한 리듬 값은 가져오지 않음");
                return;
            }

            item.Lyrics = ReadLyrics(note, item.ExtendEnds);
            item.TieToNext = item.Kind == "Note" && ReadTie(note);
            item.Beams = ReadBeams(note);
            ReadNotations(note);
            measure.Events.Add(item);
        }

        private static bool TrySetFraction(Rational value, Action<int, int> set)
        {
            if (value.Numerator is < 0 or > int.MaxValue || value.Denominator > int.MaxValue) return false;
            set((int)value.Numerator, (int)value.Denominator);
            return true;
        }

        private int ReadStaff(XElement note)
        {
            if (note.Element("staff") is null) return 0;
            if (int.TryParse(note.Element("staff")!.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var staff)
                && staff >= 1 && staff <= _staves)
                return staff - 1;

            Warn("없는 보표를 가리키는 음표는 첫 번째 보표로 옮김");
            return 0;
        }

        private static int ReadVoice(XElement note) =>
            int.TryParse(note.Element("voice")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var voice) && voice >= 1
                ? voice
                : 1;

        private bool ReadPitch(XElement pitch, ScoreEvent item)
        {
            var step = pitch.Element("step")?.Value.Trim().ToUpperInvariant();
            var semitone = step switch { "C" => 0, "D" => 2, "E" => 4, "F" => 5, "G" => 7, "A" => 9, "B" => 11, _ => -1 };
            if (semitone < 0
                || !int.TryParse(pitch.Element("octave")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var octave))
                return false;

            var alter = 0;
            if (pitch.Element("alter") is { } alterElement)
            {
                if (!decimal.TryParse(alterElement.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
                    return false;
                alter = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
                if (alter != exact) Warn("미분음은 가장 가까운 반음으로 바꿈");
                if (alter is < -3 or > 3) return false;
            }

            var midi = (octave + 1) * 12 + semitone + alter;
            if (midi is < 0 or > 127) return false;

            item.MidiPitch = midi;
            item.NotationSpelling = step + (alter > 0 ? new string('#', alter) : new string('b', -alter)) + octave.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Lyrics marked placement="above" without a position sit 2.5 spaces above the staff.</summary>
        private const double AboveStaffLyricY = 2.5;

        /// <param name="extendEnds">Receives the verses whose extender line ends at this note (a lyric with only &lt;extend type="stop"&gt;).</param>
        private List<ScoreLyric> ReadLyrics(XElement note, List<int> extendEnds)
        {
            var lyrics = new List<ScoreLyric>();
            var index = 0;
            foreach (var element in note.Elements("lyric"))
            {
                index++;
                var verse = int.TryParse((string?)element.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 1
                    ? number
                    : index;

                // Several <text> elements mean an elision: two syllables sung on one note, joined with an undertie.
                var text = string.Join("‿", element.Elements("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0));
                if (text.Length == 0)
                {
                    // Only the end of an extender line.
                    if ((string?)element.Element("extend")?.Attribute("type") == "stop" && !extendEnds.Contains(verse))
                        extendEnds.Add(verse);
                    continue;
                }

                if (lyrics.Any(l => l.Verse == verse))
                {
                    Warn("같은 절 번호의 가사가 한 음표에 두 번 있으면 처음 것만 가져옴");
                    continue;
                }

                var syllabic = element.Elements("syllabic").FirstOrDefault()?.Value.Trim() switch
                {
                    "begin" => LyricSyllabic.Begin,
                    "middle" => LyricSyllabic.Middle,
                    "end" => LyricSyllabic.End,
                    _ => LyricSyllabic.Single,
                };
                var extend = element.Element("extend") is { } extendElement && IsStartOrSingle(extendElement);

                // default-y is in tenths above the staff's top line (lyrics below the staff are negative).
                double? y = double.TryParse((string?)element.Attribute("default-y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tenths)
                    && Math.Abs(tenths) <= 2000
                        ? tenths / 10
                        : (string?)element.Attribute("placement") == "above" ? AboveStaffLyricY : null;
                lyrics.Add(new ScoreLyric { Verse = verse, Text = text, Syllabic = syllabic, Extend = extend, Y = y });
            }

            return lyrics.OrderBy(l => l.Verse).ToList();
        }

        /// <summary>
        /// &lt;beam number="n"&gt; for each beam level at this note. Levels must run 1, 2, … without gaps; anything else
        /// is dropped with a warning and the note keeps its flag.
        /// </summary>
        private List<BeamType> ReadBeams(XElement note)
        {
            var beams = new SortedDictionary<int, BeamType>();
            foreach (var beam in note.Elements("beam"))
            {
                var number = beam.Attribute("number") is { } attribute
                    && int.TryParse(attribute.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
                BeamType? type = beam.Value.Trim() switch
                {
                    "begin" => BeamType.Begin,
                    "continue" => BeamType.Continue,
                    "end" => BeamType.End,
                    "forward hook" => BeamType.ForwardHook,
                    "backward hook" => BeamType.BackwardHook,
                    _ => null,
                };

                if (type is null || number < 1 || number > 6 || !beams.TryAdd(number, type.Value))
                {
                    Warn("읽을 수 없는 빔(음표 잇기) 정보는 무시함");
                    return new List<BeamType>();
                }
            }

            if (beams.Count > 0 && beams.Keys.Last() != beams.Count)
            {
                Warn("읽을 수 없는 빔(음표 잇기) 정보는 무시함");
                return new List<BeamType>();
            }

            return beams.Values.ToList();
        }

        private bool ReadTie(XElement note)
        {
            var types = note.Elements("tie").Select(t => (string?)t.Attribute("type"))
                .Concat(note.Elements("notations").SelectMany(n => n.Elements("tied")).Select(t => (string?)t.Attribute("type")))
                .ToList();
            if (types.Contains("let-ring"))
                Warn("울림 붙임줄(let ring)");
            return types.Contains("start");
        }

        private void ReadNotations(XElement note)
        {
            var notations = note.Elements("notations").SelectMany(n => n.Elements())
                .Where(n => n.Name != "tied")
                .Where(IsStartOrSingle);
            foreach (var name in notations.Select(n => n.Name.LocalName).Distinct())
                Warn(NotationNames.TryGetValue(name, out var label) ? label : $"기호 <{name}>");
        }

        private void ReadBarline(XElement barline, ScoreMeasure measure, int index)
        {
            if (barline.Element("repeat") is { } repeat)
            {
                if ((string?)repeat.Attribute("direction") == "forward")
                {
                    measure.RepeatStart = true;
                }
                else
                {
                    measure.RepeatEnd = true;
                    if (repeat.Attribute("times") is { } timesAttribute)
                    {
                        if (int.TryParse(timesAttribute.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var times) && times >= 2)
                            measure.RepeatTimes = times;
                        else
                            Warn("읽을 수 없는 반복 횟수는 2회로 가져옴");
                    }
                }
            }

            if (barline.Element("ending") is { } ending)
                ReadEnding(ending, measure, index);

            if (barline.Element("fermata") is not null) Warn("페르마타");
            if (barline.Element("segno") is not null || barline.Element("coda") is not null) Warn("세뇨·코다");
        }

        /// <summary>
        /// A volta starts with type="start" on one measure and ends with type="stop" (closed hook) or
        /// "discontinue" (open end) on the same or a later measure.
        /// </summary>
        private void ReadEnding(XElement ending, ScoreMeasure measure, int index)
        {
            var type = (string?)ending.Attribute("type");
            if (type == "start")
            {
                if (_openVolta is not null)
                {
                    Warn("끝나지 않은 볼타(1·2번 괄호)는 마지막 마디까지로 맞춤");
                    _openVolta.MeasureCount = index - _openVoltaStart;
                    _openVolta.OpenEnd = true;
                }

                var numbers = ((string?)ending.Attribute("number"))?.Trim();
                _openVolta = new ScoreVolta
                {
                    Numbers = string.IsNullOrEmpty(numbers) ? "1" : numbers,
                    Text = ending.Value.Trim(),
                };
                _openVoltaStart = index;
                measure.Volta = _openVolta;
            }
            else if (type is "stop" or "discontinue" && _openVolta is not null)
            {
                _openVolta.MeasureCount = index - _openVoltaStart + 1;
                _openVolta.OpenEnd = type == "discontinue";
                _openVolta = null;
            }
        }

        private void ReadHarmony(XElement harmony, ScoreMeasure measure, Rational cursor)
        {
            var root = harmony.Element("root");
            var rootStep = root?.Element("root-step")?.Value.Trim().ToUpperInvariant();
            if (root is null || rootStep is not ("A" or "B" or "C" or "D" or "E" or "F" or "G"))
            {
                Warn(harmony.Element("function") is not null || harmony.Element("numeral") is not null
                    ? "로마 숫자 화성 기호"
                    : "읽을 수 없는 코드 이름");
                return;
            }

            if (harmony.Element("frame") is not null)
                Warn("코드 다이어그램(기타 프렛 그림)");

            var position = cursor;
            if (harmony.Element("offset") is { } offset
                && long.TryParse(offset.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offsetDivisions)
                && _divisions > 0)
            {
                position += new Rational(offsetDivisions, _divisions);
                if (position.Sign < 0) position = Rational.Zero;
            }

            var chord = new ScoreChordSymbol
            {
                RootStep = rootStep,
                RootAlter = Alter(root.Element("root-alter")),
                Kind = harmony.Element("kind")?.Value.Trim() is { Length: > 0 } kind ? kind : "major",
                Staff = harmony.Element("staff") is { } staffElement
                    && int.TryParse(staffElement.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var staff)
                    && staff >= 1 && staff <= _staves
                        ? staff - 1
                        : 0,
            };

            if (harmony.Element("bass") is { } bass
                && bass.Element("bass-step")?.Value.Trim().ToUpperInvariant() is "A" or "B" or "C" or "D" or "E" or "F" or "G")
            {
                chord.BassStep = bass.Element("bass-step")!.Value.Trim().ToUpperInvariant();
                chord.BassAlter = Alter(bass.Element("bass-alter"));
            }

            if (!TrySetFraction(position, (n, d) => (chord.PositionNumerator, chord.PositionDenominator) = (n, d)))
            {
                Warn("읽을 수 없는 코드 이름");
                return;
            }

            chord.Text = ChordText(chord, harmony);
            if (double.TryParse((string?)harmony.Attribute("default-y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tenths)
                && Math.Abs(tenths) <= 2000)
                chord.Y = tenths / 10;
            measure.ChordSymbols.Add(chord);
        }

        private static int Alter(XElement? element) =>
            decimal.TryParse(element?.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var alter)
                ? Math.Clamp((int)Math.Round(alter, MidpointRounding.AwayFromZero), -2, 2)
                : 0;

        private static string Accidental(int alter) => alter > 0 ? new string('#', alter) : new string('b', -alter);

        /// <summary>
        /// The printed chord symbol. The kind's own text (what Finale shows) wins; otherwise a common
        /// lead-sheet suffix is used. Added, altered and removed degrees are appended.
        /// </summary>
        private static string ChordText(ScoreChordSymbol chord, XElement harmony)
        {
            var kindElement = harmony.Element("kind");
            if (chord.Kind == "none")
                return "N.C.";

            var suffix = (string?)kindElement?.Attribute("text") ?? chord.Kind switch
            {
                "major" => "",
                "minor" => "m",
                "augmented" => "+",
                "diminished" => "dim",
                "dominant" => "7",
                "major-seventh" => "maj7",
                "minor-seventh" => "m7",
                "diminished-seventh" => "dim7",
                "augmented-seventh" => "+7",
                "half-diminished" => "m7b5",
                "major-minor" => "m(maj7)",
                "major-sixth" => "6",
                "minor-sixth" => "m6",
                "dominant-ninth" => "9",
                "major-ninth" => "maj9",
                "minor-ninth" => "m9",
                "dominant-11th" => "11",
                "major-11th" => "maj11",
                "minor-11th" => "m11",
                "dominant-13th" => "13",
                "major-13th" => "maj13",
                "minor-13th" => "m13",
                "suspended-second" => "sus2",
                "suspended-fourth" => "sus4",
                "power" => "5",
                _ => "",
            };

            // Degrees marked print-object="no" only describe the chord; the kind's text already shows what is printed.
            foreach (var degree in harmony.Elements("degree").Where(d => (string?)d.Attribute("print-object") != "no"))
            {
                var value = degree.Element("degree-value")?.Value.Trim() ?? "";
                var alter = Accidental(Alter(degree.Element("degree-alter")));
                suffix += degree.Element("degree-type")?.Value.Trim() switch
                {
                    "add" => $"add{alter}{value}",
                    "subtract" => $"no{value}",
                    _ => $"{alter}{value}",
                };
            }

            var text = chord.RootStep + Accidental(chord.RootAlter) + suffix;
            return chord.BassStep is null ? text : $"{text}/{chord.BassStep}{Accidental(chord.BassAlter)}";
        }

        /// <summary>
        /// Slurs, ties, tuplets, wedges and similar marks have a start and a stop element. Count each mark once, at its start.
        /// </summary>
        private static bool IsStartOrSingle(XElement element) =>
            (string?)element.Attribute("type") is not ("stop" or "continue");
    }
}
