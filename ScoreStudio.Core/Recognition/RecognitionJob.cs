using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Recognition;

/// <summary>The outcome of recognising pictures: the joined score as an import (with its warnings), and notes for the user.</summary>
public sealed record RecognitionResult(ImportResult Import, IReadOnlyList<string> Notes)
{
    public ScoreProject Score => Import.Project;

    public IReadOnlyList<ImportWarning> Warnings => Import.Warnings;
}

/// <summary>
/// Turns PDF files and pictures into one score with Audiveris (docs/OMR_IMPORT.md): enlarges pictures whose staff
/// lines are too close, recognises everything in one Audiveris run, imports the MusicXML and joins the results.
/// </summary>
public sealed class RecognitionJob
{
    public static readonly IReadOnlyList<string> PictureExtensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    public required string Audiveris { get; init; }
    public required IImageReader Pictures { get; init; }

    /// <summary>OCR languages for Audiveris ("kor+eng"), or null to recognise no text.</summary>
    public string? Languages { get; init; }

    /// <summary>A folder of its own for this job; it is created and filled with the prepared inputs and Audiveris' output.</summary>
    public required string WorkFolder { get; init; }

    private const string PaperSizeWarning = "지원하지 않는 용지 크기";

    public static bool IsSupported(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        || PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Time allowed for Audiveris: three minutes, and three more for each input.</summary>
    public static TimeSpan Timeout(int inputs) => TimeSpan.FromMinutes(3 + 3 * inputs);

    public async Task<RecognitionResult> RunAsync(IReadOnlyList<string> files, IProgress<string>? log = null, CancellationToken cancellation = default)
    {
        if (files.Count == 0) throw new ArgumentException("인식할 파일이 없습니다.", nameof(files));
        var unsupported = files.Where(f => !IsSupported(f)).ToList();
        if (unsupported.Count > 0) throw new NotSupportedException($"인식할 수 없는 파일 형식입니다: {string.Join(", ", unsupported.Select(Path.GetFileName))}");

        var inputFolder = Path.Combine(WorkFolder, "input");
        var outputFolder = Path.Combine(WorkFolder, "output");
        Directory.CreateDirectory(inputFolder);
        var notes = new List<string>();

        // Each input gets a numbered name, so that Audiveris' outputs cannot clash and keep the order.
        var inputs = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var file = files[i];
            var target = Path.Combine(inputFolder, $"{i + 1:D2}-{SafeName(Path.GetFileNameWithoutExtension(file))}");
            if (Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, target + ".pdf", overwrite: true);
                inputs.Add(target + ".pdf");
                continue;
            }

            var image = Pictures.Read(file);
            var spacing = StaffSpacing.Estimate(image);
            var factor = ImageScaling.Factor(spacing);
            log?.Report(spacing is { } s
                ? $"{Path.GetFileName(file)}: 오선 간격 {s:0.#}픽셀{(factor > 1 ? $", {factor}배로 키움" : "")}"
                : $"{Path.GetFileName(file)}: 오선 간격을 잴 수 없어 {factor}배로 키움");
            PngCodec.Write(ImageScaling.Enlarge(image, factor), target + ".png");
            inputs.Add(target + ".png");
        }

        var recognized = await AudiverisRunner.RunAsync(Audiveris, inputs, outputFolder, Languages, Timeout(inputs.Count), log, cancellation);

        var scores = new List<ScoreProject>();
        var warnings = new List<ImportWarning>();
        var sheets = new List<SheetPicture>();
        (double Left, double Right)? systemMargins = null;
        for (var i = 0; i < recognized.Count; i++)
        {
            if (recognized[i].Book is { } book)
            {
                try
                {
                    sheets.AddRange(SourcePictures.FromBook(book, Path.GetFileName(files[i])));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    notes.Add($"{Path.GetFileName(files[i])}의 원본 그림을 읽지 못해 비교 화면에 넣지 않았습니다.");
                }
            }

            if (recognized[i].Scores.Count == 0)
            {
                notes.Add($"{Path.GetFileName(files[i])}에서 악보를 찾지 못했습니다(Audiveris 기록을 확인하세요).");
                continue;
            }

            foreach (var score in recognized[i].Scores)
            {
                var imported = MusicXmlImporter.ImportFile(score);
                if (imported.Project.Parts.Count == 0 || imported.Project.Parts.All(p => p.Measures.Count == 0)) continue;
                scores.Add(imported.Project);
                systemMargins ??= SourcePictures.SystemMargins(score);
                // Audiveris gives the picture's size as the page: no paper size to warn about.
                warnings.AddRange(imported.Warnings.Where(w => !w.Message.StartsWith(PaperSizeWarning, StringComparison.Ordinal)));
            }
        }

        if (scores.Count == 0) throw new InvalidOperationException("어느 파일에서도 악보를 인식하지 못했습니다. 그림이 선명한지, 오선이 잘리지 않았는지 확인하세요.");

        // Each picture's music starts a page, so that page by page the score lines up with the pictures.
        var (joined, joinNotes) = ScoreMerge.Join(scores, newPage: sheets.Count > 0);
        notes.AddRange(joinNotes);
        SourcePictures.Attach(joined, sheets, systemMargins);
        if (string.IsNullOrWhiteSpace(joined.Title) || joined.Title == "새 악보") joined.Title = Path.GetFileNameWithoutExtension(files[0]);
        joined.ImportedFrom = files[0];
        notes.Add("인식 결과에는 틀린 곳이 있을 수 있습니다. 원본과 비교해 고치세요.");
        return new RecognitionResult(new ImportResult { Project = joined, Warnings = Merge(warnings), Summary = ImportSummary.Of(joined) }, notes);
    }

    /// <summary>
    /// The input's name for Audiveris: plain ASCII letters and digits only, so no program on the way can garble it
    /// (the numbered prefix already keeps names apart).
    /// </summary>
    public static string SafeName(string name)
    {
        var safe = new string(name.Where(char.IsAsciiLetterOrDigit).Take(40).ToArray());
        return safe.Length == 0 ? "page" : safe;
    }

    /// <summary>Warnings of the same kind from several results added up, keeping the first place.</summary>
    private static List<ImportWarning> Merge(List<ImportWarning> warnings) =>
        warnings.GroupBy(w => w.Message).Select(g => new ImportWarning(g.Key, g.Sum(w => w.Count), g.First().FirstLocation)).ToList();
}

/// <summary>Orders file names as people number them: "캡처 2" before "캡처 10".</summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a is null || b is null) return string.Compare(a, b, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var (startA, startB) = (i, j);
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var (numberA, numberB) = (a[startA..i].TrimStart('0'), b[startB..j].TrimStart('0'));
                var byNumber = numberA.Length != numberB.Length ? numberA.Length.CompareTo(numberB.Length) : string.CompareOrdinal(numberA, numberB);
                if (byNumber != 0) return byNumber;
                continue;
            }

            var byChar = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (byChar != 0) return byChar;
            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}
