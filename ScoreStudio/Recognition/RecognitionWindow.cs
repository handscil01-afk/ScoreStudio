using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScoreStudio.Recognition;

namespace ScoreStudio.RecognitionUi;

/// <summary>
/// Recognises the chosen PDF files and pictures with Audiveris (docs/OMR_IMPORT.md): fetches the OCR language files
/// when missing, shows Audiveris' log while it works, and can be cancelled. Closes with true and <see cref="Result"/>
/// when a score came out; on failure it stays open with the reason.
/// </summary>
public sealed class RecognitionWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _stage = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Height = 12, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas, 맑은 고딕"), FontSize = 11,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly Button _button = new() { Content = "취소", Padding = new Thickness(18, 4, 18, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
    private bool _running;

    public RecognitionWindow(string audiveris, IReadOnlyList<string> files)
    {
        Title = "악보 인식 (Audiveris)";
        Width = 680;
        Height = 440;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_stage, Dock.Top);
        DockPanel.SetDock(_bar, Dock.Top);
        DockPanel.SetDock(_button, Dock.Bottom);
        panel.Children.Add(_stage);
        panel.Children.Add(_bar);
        panel.Children.Add(_button);
        panel.Children.Add(_log);
        Content = panel;

        _button.Click += (_, _) =>
        {
            if (_running) Cancel();
            else Close();
        };
        Closing += (_, e) =>
        {
            if (!_running) return;
            e.Cancel = true; // closes once Audiveris has stopped
            Cancel();
        };
        Loaded += async (_, _) => await RunAsync(audiveris, files);
    }

    public RecognitionResult? Result { get; private set; }

    private void Cancel()
    {
        _stage.Text = "취소하는 중입니다...";
        _button.IsEnabled = false;
        _cancel.Cancel();
    }

    private void Append(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
    }

    private async Task RunAsync(string audiveris, IReadOnlyList<string> files)
    {
        _running = true;
        var log = new Progress<string>(Append);
        var work = Path.Combine(Path.GetTempPath(), "ScoreStudio", "recognition", Guid.NewGuid().ToString("N"));
        try
        {
            var tessdata = OcrLanguages.TessdataFolder();
            var missing = OcrLanguages.Missing(tessdata);
            if (missing.Count > 0)
            {
                _stage.Text = "가사·제목을 읽을 언어 파일(한국어·영어)을 받는 중입니다. 처음 한 번만 받습니다.";
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                foreach (var language in missing)
                {
                    try
                    {
                        await OcrLanguages.DownloadAsync(http, OcrLanguages.DownloadUrl(language), tessdata, language, _cancel.Token);
                        Append($"{language} 언어 파일을 받았습니다: {tessdata}");
                    }
                    catch (Exception ex) when (!_cancel.IsCancellationRequested && ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
                    {
                        Append($"{language} 언어 파일을 받지 못했습니다({ex.Message}). 그 언어의 글자는 읽지 않고 계속합니다.");
                    }
                }
            }

            var languages = OcrLanguages.Specification(tessdata);
            _stage.Text = $"Audiveris로 {files.Count}개 파일을 인식하는 중입니다. 한 쪽에 10~30초쯤 걸립니다.";
            Append($"Audiveris: {audiveris}");
            Append($"글자 인식 언어: {languages ?? "없음 (가사·제목을 읽지 않음)"}");
            var job = new RecognitionJob { Audiveris = audiveris, Pictures = new WpfImageReader(), Languages = languages, WorkFolder = work };
            Result = await Task.Run(() => job.RunAsync(files, log, _cancel.Token));
            _running = false;
            TryDelete(work);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            _running = false;
            TryDelete(work);
            DialogResult = false;
        }
        catch (Exception ex)
        {
            _running = false;
            _bar.Visibility = Visibility.Collapsed;
            _stage.Text = $"악보를 인식하지 못했습니다. {ex.Message}";
            Append($"오류: {ex.GetType().Name}: {ex.Message}");
            Append($"작업 폴더(Audiveris 기록 포함): {work}");
            _button.Content = "닫기";
            _button.IsEnabled = true;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temporary files only; Windows cleans the temp folder eventually.
        }
    }
}
