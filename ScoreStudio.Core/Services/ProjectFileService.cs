using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScoreStudio.Models;

namespace ScoreStudio.Services;

public static class ProjectFileService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Save(string path, ScoreProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        project.SchemaVersion = ScoreProject.CurrentSchemaVersion;
        project.ModifiedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(project, Options);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectFileException($"저장할 파일 경로가 올바르지 않습니다.\n{path}", ex);
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ProjectFileException("저장 폴더를 확인할 수 없습니다.");

        // Write to a temporary file first so a failed save never leaves a half-written project behind.
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(tempPath, json, Utf8NoBom);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new ProjectFileException($"파일을 저장할 수 없습니다.\n{fullPath}\n\n{ex.Message}", ex);
        }
    }

    public static ScoreProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (FileNotFoundException ex)
        {
            throw new ProjectFileException($"파일을 찾을 수 없습니다.\n{path}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new ProjectFileException($"파일이 있는 폴더를 찾을 수 없습니다.\n{path}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ProjectFileException($"파일을 읽을 수 없습니다.\n{path}\n\n{ex.Message}", ex);
        }

        return Parse(json);
    }

    /// <summary>Parses and validates project JSON. Throws <see cref="ProjectFileException"/> for any invalid input.</summary>
    public static ScoreProject Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ProjectFileException("파일이 비어 있습니다. ScoreStudio 프로젝트 파일이 아니거나 저장 중에 손상되었을 수 있습니다.");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ProjectFileException("ScoreStudio 프로젝트 파일이 아닙니다. 파일의 최상위 데이터가 프로젝트 형식이 아닙니다.");

            CheckSchemaVersion(root);

            var project = root.Deserialize<ScoreProject>(Options)
                ?? throw new ProjectFileException("프로젝트 데이터가 비어 있습니다.");
            Validate(project);
            return project;
        }
        catch (JsonException ex)
        {
            // The parser's own message is English and technical, so it stays in InnerException only.
            var location = ex.LineNumber is long line ? $" ({line + 1}번째 줄 부근)" : "";
            throw new ProjectFileException(
                $"프로젝트 파일이 손상되었거나 형식이 올바르지 않습니다{location}.", ex);
        }
    }

    private static void CheckSchemaVersion(JsonElement root)
    {
        JsonElement? versionElement = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, nameof(ScoreProject.SchemaVersion), StringComparison.OrdinalIgnoreCase))
                versionElement = property.Value;
        }

        if (versionElement is not { } element)
            throw new ProjectFileException("프로젝트 파일에 버전 정보(SchemaVersion)가 없습니다. ScoreStudio 프로젝트 파일이 아닐 수 있습니다.");

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var version))
            throw new ProjectFileException("프로젝트 파일의 버전 정보(SchemaVersion)가 올바르지 않습니다.");

        if (version > ScoreProject.CurrentSchemaVersion)
            throw new ProjectFileException(
                $"더 새로운 버전의 ScoreStudio에서 만든 파일입니다(파일 버전 {version}, 지원 버전 {ScoreProject.CurrentSchemaVersion}). 프로그램을 업데이트한 뒤 다시 열어 주세요.");

        if (version < 1)
            throw new ProjectFileException($"지원하지 않는 프로젝트 버전입니다: {version}");

        // Version 1 files have no staves; Validate fills them in. Saving always writes the current version.
    }

    /// <summary>Fills in missing optional values and rejects data that would break later code.</summary>
    private static void Validate(ScoreProject project)
    {
        project.Title ??= "";
        project.Composer ??= "";
        project.PageSize ??= "A4";
        project.Orientation ??= "Portrait";
        project.Parts ??= new List<ScorePart>();

        for (var p = 0; p < project.Parts.Count; p++)
        {
            var part = project.Parts[p]
                ?? throw Corrupt($"{p + 1}번째 파트 데이터가 비어 있습니다.");
            part.Id = string.IsNullOrWhiteSpace(part.Id) ? Guid.NewGuid().ToString("N") : part.Id;
            part.Name ??= "";
            part.Instrument ??= "Unknown";
            part.Measures ??= new List<ScoreMeasure>();

            if (part.Staves is null || part.Staves.Count == 0)
                part.Staves = new List<ScoreStaff> { new() };
            for (var s = 0; s < part.Staves.Count; s++)
            {
                var staff = part.Staves[s]
                    ?? throw Corrupt($"{p + 1}번째 파트의 {s + 1}번째 보표 데이터가 비어 있습니다.");
                if (!Enum.IsDefined(staff.Clef))
                    throw Corrupt($"{p + 1}번째 파트의 {s + 1}번째 보표에 알 수 없는 음자리표가 있습니다.");
            }

            for (var m = 0; m < part.Measures.Count; m++)
            {
                var where = $"{p + 1}번째 파트의 {m + 1}번째 마디";
                var measure = part.Measures[m] ?? throw Corrupt($"{where} 데이터가 비어 있습니다.");
                if (measure.Numerator <= 0 || measure.Denominator <= 0)
                    throw Corrupt($"{where}의 박자표({measure.Numerator}/{measure.Denominator})가 올바르지 않습니다.");
                measure.Events ??= new List<ScoreEvent>();

                for (var e = 0; e < measure.Events.Count; e++)
                {
                    var item = measure.Events[e] ?? throw Corrupt($"{where}의 {e + 1}번째 음표/쉼표 데이터가 비어 있습니다.");
                    item.Kind ??= "Note";
                    if (item.DurationNumerator <= 0 || item.DurationDenominator <= 0)
                        throw Corrupt($"{where}의 {e + 1}번째 음표/쉼표 길이가 올바르지 않습니다.");
                    if (item.PositionNumerator < 0 || item.PositionDenominator <= 0)
                        throw Corrupt($"{where}의 {e + 1}번째 음표/쉼표 위치가 올바르지 않습니다.");
                    if (item.MidiPitch is < 0 or > 127)
                        throw Corrupt($"{where}의 {e + 1}번째 음높이({item.MidiPitch})가 범위(0~127)를 벗어났습니다.");
                }
            }
        }
    }

    private static ProjectFileException Corrupt(string detail) =>
        new($"프로젝트 파일의 악보 데이터가 손상되었습니다.\n{detail}");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original save error matters more than a leftover temp file.
        }
    }
}
