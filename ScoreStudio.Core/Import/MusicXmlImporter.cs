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

        foreach (var partElement in root.Elements("part"))
        {
            var id = (string?)partElement.Attribute("id") ?? "";
            if (!partList.Remove(id, out var scorePart))
                throw new ProjectFileException($"파트 목록(part-list)에 없는 파트('{id}')가 있습니다. MusicXML 파일이 손상되었을 수 있습니다.");

            var name = FirstText(scorePart.Element("part-name")) ?? $"파트 {project.Parts.Count + 1}";
            var instrument = FirstText(scorePart.Element("score-instrument")?.Element("instrument-name")) ?? name;
            project.Parts.Add(new PartReader(name, instrument, warnings).Read(partElement));
        }

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
                Lyrics = events.Count(e => !string.IsNullOrEmpty(e.Lyric)),
            },
        };
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
            ["octave-shift"] = "옥타브 기호(8va 등)",
            ["rehearsal"] = "리허설 마크",
            ["segno"] = "세뇨·코다",
            ["coda"] = "세뇨·코다",
        };

        private static readonly HashSet<string> IgnoredMeasureChildren = new() { "print", "sound", "listening", "bookmark", "link", "grouping" };

        private readonly string _name;
        private readonly string _instrument;
        private readonly ImportWarningCollector _warnings;
        private readonly Dictionary<int, ClefType> _clefs = new();

        private long _divisions;
        private int _numerator = 4;
        private int _denominator = 4;
        private int _keyFifths;
        private int _staves = 1;
        private bool _firstMeasureDone;
        private string _location = "";

        public PartReader(string name, string instrument, ImportWarningCollector warnings)
        {
            _name = name;
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
            _location = $"{_name} {numberText ?? number.ToString(CultureInfo.InvariantCulture)}마디";
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
                        ReadAttributes(child, measure);
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
                        foreach (var type in child.Elements("direction-type").SelectMany(d => d.Elements()).Where(IsStartOrSingle))
                            Warn(DirectionNames.TryGetValue(type.Name.LocalName, out var label) ? label : $"지시 기호 <{type.Name.LocalName}>");
                        break;
                    case "barline":
                        if (child.Element("repeat") is not null) Warn("도돌이표");
                        if (child.Element("ending") is not null) Warn("볼타(1·2번 괄호)");
                        if (child.Element("fermata") is not null) Warn("페르마타");
                        if (child.Element("segno") is not null || child.Element("coda") is not null) Warn("세뇨·코다");
                        break;
                    case "harmony":
                        Warn("코드 이름");
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

            return measure;
        }

        private void ReadAttributes(XElement attributes, ScoreMeasure measure)
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
                ReadClef(clef);

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

        private void ReadClef(XElement clef)
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

            if (_clefs.TryGetValue(staff, out var existing))
            {
                if (existing != type) Warn("중간 음자리표 변경은 가져오지 않음 (처음 음자리표를 유지)");
                return;
            }

            _clefs[staff] = type.Value;
            _staves = Math.Max(_staves, staff);
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

            item.Lyric = ReadLyrics(note);
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

        private string? ReadLyrics(XElement note)
        {
            var lyrics = note.Elements("lyric").ToList();
            if (lyrics.Count == 0) return null;

            var first = lyrics.FirstOrDefault(l => (string?)l.Attribute("number") is "1" or null) ?? lyrics[0];
            if (lyrics.Count > 1) Warn("두 번째 이후 가사 절");
            // A hyphen follows every "begin" and "middle" syllable.
            if (first.Elements("syllabic").Any(s => s.Value.Trim() is "begin" or "middle"))
                Warn("가사 음절 연결(하이픈) 표시");
            if (first.Element("extend") is not null)
                Warn("가사 연장선");

            var text = string.Join(" ", first.Elements("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0));
            return text.Length == 0 ? null : text;
        }

        private void ReadNotations(XElement note)
        {
            var notations = note.Elements("notations").SelectMany(n => n.Elements()).Where(IsStartOrSingle).ToList();
            if (note.Elements("tie").Any(IsStartOrSingle) || notations.Any(n => n.Name == "tied"))
                Warn("붙임줄");

            foreach (var name in notations.Select(n => n.Name.LocalName).Where(n => n != "tied").Distinct())
                Warn(NotationNames.TryGetValue(name, out var label) ? label : $"기호 <{name}>");
        }

        /// <summary>
        /// Slurs, ties, tuplets, wedges and similar marks have a start and a stop element. Count each mark once, at its start.
        /// </summary>
        private static bool IsStartOrSingle(XElement element) =>
            (string?)element.Attribute("type") is not ("stop" or "continue");
    }
}
