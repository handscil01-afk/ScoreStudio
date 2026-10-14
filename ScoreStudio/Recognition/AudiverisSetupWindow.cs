using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScoreStudio.Recognition;
using ScoreStudio.Services;

namespace ScoreStudio.RecognitionUi;

/// <summary>
/// Shown when Audiveris is not installed: installs it with winget, opens its download page, or lets the user point at
/// an installed copy (kept in the settings). Closes with true and <see cref="Found"/> once Audiveris is there.
/// </summary>
public sealed class AudiverisSetupWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkRed, Margin = new Thickness(0, 10, 0, 0) };

    public AudiverisSetupWindow(AppSettings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        Title = "Audiveris 설치";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "악보 인식 프로그램 Audiveris가 필요합니다", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12),
            Text = "PDF와 그림의 악보를 읽는 일은 무료 공개 프로그램 Audiveris(약 70MB, Java 포함)가 합니다. "
                + "한 번 설치하면 ScoreStudio가 알아서 찾아 씁니다. Audiveris는 AGPL-3.0 라이선스이며 공식 GitHub에서 받습니다.",
        });
        panel.Children.Add(ActionRow("winget으로 설치 (권장)", "Windows의 앱 설치 관리자로 설치합니다. 설치 창과 관리자 확인 창이 뜹니다.", InstallWithWinget));
        panel.Children.Add(ActionRow("내려받기 페이지 열기", "Audiveris-<버전>-windows-x86_64.msi 파일을 받아 설치한 뒤 '다시 찾기'를 누르세요.", OpenDownloadPage));
        panel.Children.Add(ActionRow("설치한 위치 고르기...", "다른 곳에 설치했다면 Audiveris.exe를 직접 고릅니다.", ChooseLocation));
        panel.Children.Add(ActionRow("다시 찾기", "설치를 마쳤으면 누르세요.", () => TryFind(report: true)));
        panel.Children.Add(_status);
        var close = new Button { Content = "닫기", Padding = new Thickness(18, 4, 18, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        panel.Children.Add(close);
        Content = panel;
    }

    /// <summary>Audiveris' path, once found.</summary>
    public string? Found { get; private set; }

    private static UIElement ActionRow(string label, string hint, Action run)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), MinWidth = 170, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => run();
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(button, Dock.Left);
        row.Children.Add(button);
        row.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = System.Windows.Media.Brushes.DimGray });
        return row;
    }

    private async void InstallWithWinget()
    {
        try
        {
            _status.Text = "설치 창에서 진행 상황을 확인하세요. 끝나면 자동으로 찾습니다.";
            using var process = Process.Start(new ProcessStartInfo("winget", AudiverisLocator.WingetArguments) { UseShellExecute = true });
            if (process is not null) await process.WaitForExitAsync();
            TryFind(report: true);
        }
        catch (Win32Exception)
        {
            _status.Text = "winget(앱 설치 관리자)을 찾을 수 없습니다. '내려받기 페이지 열기'로 설치 파일을 받아 설치하세요.";
        }
    }

    private void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AudiverisLocator.ReleasesPage) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            _status.Text = $"웹 브라우저를 열지 못했습니다. 이 주소로 가세요: {AudiverisLocator.ReleasesPage}";
        }
    }

    private void ChooseLocation()
    {
        var dialog = new OpenFileDialog { Title = "Audiveris.exe 고르기", Filter = "Audiveris (Audiveris.exe)|Audiveris.exe|실행 파일 (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        _settings.AudiverisPath = dialog.FileName;
        if (!_settings.Save(_settingsPath)) _status.Text = "위치를 설정 파일에 저장하지 못해 이번에만 씁니다.";
        TryFind(report: true);
    }

    private void TryFind(bool report)
    {
        if (AudiverisLocator.Find(_settings.AudiverisPath) is { } path)
        {
            Found = path;
            DialogResult = true;
        }
        else if (report)
        {
            _status.Text = "아직 Audiveris를 찾지 못했습니다. 설치가 끝났는지 확인하거나 '설치한 위치 고르기'로 Audiveris.exe를 고르세요.";
        }
    }
}
