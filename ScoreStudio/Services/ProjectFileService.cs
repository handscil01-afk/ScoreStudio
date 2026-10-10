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
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string path, ScoreProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        project.ModifiedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(project, Options);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("저장 폴더를 확인할 수 없습니다.");
        Directory.CreateDirectory(directory);

        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, fullPath, overwrite: true);
    }

    public static ScoreProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<ScoreProject>(json, Options)
            ?? throw new InvalidDataException("프로젝트 데이터가 비어 있거나 올바르지 않습니다.");

        if (project.SchemaVersion != 1)
            throw new InvalidDataException($"지원하지 않는 프로젝트 버전입니다: {project.SchemaVersion}");

        project.Parts ??= new List<ScorePart>();
        return project;
    }
}
