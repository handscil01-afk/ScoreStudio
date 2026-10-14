using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ScoreStudio.Recognition;

/// <summary>Where Audiveris may be installed (docs/OMR_IMPORT.md, step 2).</summary>
public static class AudiverisLocator
{
    /// <summary>
    /// The places to look, in order: the path the user chose, the Windows installer's folders (all users, then this
    /// user), scoop, and the PATH. <paramref name="environment"/> reads environment variables.
    /// </summary>
    public static IEnumerable<string> Candidates(string? chosen, Func<string, string?> environment)
    {
        if (!string.IsNullOrWhiteSpace(chosen)) yield return chosen;
        foreach (var folder in new[] { "ProgramFiles", "ProgramW6432", "LOCALAPPDATA" })
        {
            if (environment(folder) is not { Length: > 0 } root) continue;
            yield return folder == "LOCALAPPDATA"
                ? Path.Combine(root, "Programs", "Audiveris", "Audiveris.exe")
                : Path.Combine(root, "Audiveris", "Audiveris.exe");
        }

        if (environment("USERPROFILE") is { Length: > 0 } profile)
            yield return Path.Combine(profile, "scoop", "apps", "audiveris", "current", "Audiveris.exe");

        foreach (var directory in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(directory, "Audiveris.exe");
            yield return Path.Combine(directory, "Audiveris");
            yield return Path.Combine(directory, "audiveris");
        }

        // The Linux and macOS installers.
        yield return "/opt/audiveris/bin/Audiveris";
        yield return "/Applications/Audiveris.app/Contents/MacOS/Audiveris";
    }

    /// <summary>The first candidate that exists, or null.</summary>
    public static string? Find(string? chosen, Func<string, string?> environment, Func<string, bool> exists) =>
        Candidates(chosen, environment).FirstOrDefault(exists);

    public static string? Find(string? chosen) => Find(chosen, Environment.GetEnvironmentVariable, File.Exists);

    /// <summary>The winget command that installs Audiveris on Windows.</summary>
    public const string WingetArguments = "install --exact --id audiveris.org.Audiveris --accept-package-agreements --accept-source-agreements";

    public const string ReleasesPage = "https://github.com/Audiveris/audiveris/releases";
}

/// <summary>What Audiveris made of one input: its scores (one per movement) in order, none when it failed.</summary>
public sealed record RecognizedInput(string Input, IReadOnlyList<string> Scores);

/// <summary>Runs Audiveris in batch mode and collects the MusicXML files it exports (docs/OMR_IMPORT.md, step 5).</summary>
public static class AudiverisRunner
{
    /// <summary>The application constant that sets the OCR languages, e.g. "kor+eng".</summary>
    public const string LanguageConstant = "org.audiveris.omr.text.Language.defaultSpecification";

    public static IReadOnlyList<string> Arguments(IEnumerable<string> inputs, string outputFolder, string? languages)
    {
        var arguments = new List<string> { "-batch", "-export" };
        if (!string.IsNullOrEmpty(languages)) arguments.AddRange(new[] { "-constant", $"{LanguageConstant}={languages}" });
        arguments.AddRange(new[] { "-output", outputFolder, "--" });
        arguments.AddRange(inputs);
        return arguments;
    }

    /// <summary>
    /// The scores exported for an input: "name.mxl", or "name.mvt1.mxl", "name.mvt2.mxl", … (also uncompressed ".xml"),
    /// ordered by movement.
    /// </summary>
    public static IReadOnlyList<string> ScoresFor(string input, string outputFolder)
    {
        var name = Path.GetFileNameWithoutExtension(input);
        var pattern = new Regex($"^{Regex.Escape(name)}(\\.mvt(?<n>\\d+))?\\.(mxl|xml)$", RegexOptions.IgnoreCase);
        return Directory.Exists(outputFolder)
            ? Directory.GetFiles(outputFolder)
                .Select(f => (File: f, Match: pattern.Match(Path.GetFileName(f))))
                .Where(f => f.Match.Success)
                .OrderBy(f => f.Match.Groups["n"].Success ? int.Parse(f.Match.Groups["n"].Value) : 0)
                .Select(f => f.File).ToList()
            : Array.Empty<string>();
    }

    /// <summary>
    /// Runs Audiveris on all inputs at once (Java starts once). Its log lines go to <paramref name="log"/>; cancelling or
    /// running out of time stops it. Success is judged by the files it exported, since it may exit with 0 on failure.
    /// </summary>
    public static async Task<IReadOnlyList<RecognizedInput>> RunAsync(
        string executable, IReadOnlyList<string> inputs, string outputFolder, string? languages, TimeSpan timeout,
        IProgress<string>? log = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(outputFolder);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var argument in Arguments(inputs, outputFolder, languages)) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log?.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log?.Report(e.Data); };
        if (!process.Start()) throw new InvalidOperationException("Audiveris를 실행하지 못했습니다.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellation.IsCancellationRequested) throw;
            throw new TimeoutException($"악보 인식이 {timeout.TotalMinutes:0}분 안에 끝나지 않아 멈췄습니다.");
        }

        return inputs.Select(input => new RecognizedInput(input, ScoresFor(input, outputFolder))).ToList();
    }
}

/// <summary>The Tesseract language files Audiveris reads lyrics and titles with (docs/OMR_IMPORT.md, Audiveris).</summary>
public static class OcrLanguages
{
    /// <summary>Korean and English, in the order Audiveris tries them.</summary>
    public static readonly IReadOnlyList<string> Wanted = new[] { "kor", "eng" };

    /// <summary>The Tesseract data version Audiveris links against (its "legacy" models).</summary>
    public const string TessdataVersion = "4.1.0";

    public static string DownloadUrl(string language) =>
        $"https://raw.githubusercontent.com/tesseract-ocr/tessdata/{TessdataVersion}/{language}.traineddata";

    /// <summary>Audiveris' configuration folder on this system, as its handbook gives it.</summary>
    public static string ConfigFolder(bool windows, bool mac, Func<string, string?> environment)
    {
        if (windows) return Path.Combine(environment("APPDATA") ?? "", "AudiverisLtd", "audiveris", "config");
        var home = environment("HOME") ?? "";
        if (mac) return Path.Combine(home, "Library", "Application Support", "AudiverisLtd", "audiveris");
        var xdg = environment("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "AudiverisLtd", "audiveris");
    }

    /// <summary>The folder Audiveris loads language files from: TESSDATA_PREFIX when it is a folder, else its config's tessdata.</summary>
    public static string TessdataFolder(bool windows, bool mac, Func<string, string?> environment, Func<string, bool> directoryExists) =>
        environment("TESSDATA_PREFIX") is { Length: > 0 } prefix && directoryExists(prefix)
            ? prefix
            : Path.Combine(ConfigFolder(windows, mac, environment), "tessdata");

    public static string TessdataFolder() =>
        TessdataFolder(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), Environment.GetEnvironmentVariable, Directory.Exists);

    public static IReadOnlyList<string> Missing(string folder) =>
        Wanted.Where(l => !File.Exists(Path.Combine(folder, $"{l}.traineddata"))).ToList();

    /// <summary>The languages to pass to Audiveris: those installed, in the wanted order, or null for none.</summary>
    public static string? Specification(string folder)
    {
        var installed = Wanted.Where(l => File.Exists(Path.Combine(folder, $"{l}.traineddata"))).ToList();
        return installed.Count == 0 ? null : string.Join("+", installed);
    }

    /// <summary>
    /// Downloads a language file into the folder: to a temporary name first, so a failed download leaves nothing that
    /// Audiveris would take for a language file.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, string url, string folder, string language, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"{language}.traineddata");
        var partial = target + ".download";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(partial);
                await response.Content.CopyToAsync(file, cancellation);
            }

            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}
