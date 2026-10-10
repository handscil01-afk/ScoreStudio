using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Rendering;
using ScoreStudio.Services;

namespace ScoreStudio;

public partial class MainWindow : Window
{
    private ScoreProject _project = ScoreProject.CreateNew();
    private bool _updatingUi;

    private static readonly double[] ZoomLevels = { 0.5, 0.75, 1, 1.25, 1.5, 2, 3 };

    private double _zoom = 1;

    public MainWindow()
    {
        InitializeComponent();
        SetZoom(1);
        ApplyProjectToUi();
    }

    private void NewScore_Click(object sender, RoutedEventArgs e)
    {
        _project = ScoreProject.CreateNew();
        ApplyProjectToUi();
        StatusText.Content = "새 악보를 만들었습니다.";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ScoreStudio 프로젝트 (*.scoreproj)|*.scoreproj|JSON 파일 (*.json)|*.json",
            Title = "프로젝트 열기"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            _project = ProjectFileService.Load(dialog.FileName);
            ApplyProjectToUi();
            StatusText.Content = $"열기 완료: {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            // The current project stays open, so nothing is lost when a file fails to open.
            ShowFileError("프로젝트를 열 수 없습니다.", "열기 오류", ex);
            StatusText.Content = "열기 실패: 현재 악보는 그대로 유지됩니다.";
        }
    }

    private void ImportMusicXml_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MusicXML (*.musicxml;*.xml;*.mxl)|*.musicxml;*.xml;*.mxl|모든 파일 (*.*)|*.*",
            Title = "MusicXML 가져오기"
        };

        if (dialog.ShowDialog() != true) return;

        ImportResult result;
        try
        {
            result = MusicXmlImporter.ImportFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowFileError("MusicXML을 가져올 수 없습니다.", "가져오기 오류", ex);
            StatusText.Content = "가져오기 실패: 현재 악보는 그대로 유지됩니다.";
            return;
        }

        _project = result.Project;
        ApplyProjectToUi();
        var fileName = System.IO.Path.GetFileName(dialog.FileName);
        StatusText.Content = $"가져오기 완료: {fileName} (경고 {result.Warnings.Count}종류) · 보관하려면 저장하세요.";
        new ImportReportWindow(fileName, result) { Owner = this }.ShowDialog();
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "ScoreStudio 프로젝트 (*.scoreproj)|*.scoreproj",
            DefaultExt = ".scoreproj",
            FileName = $"{SafeFileName(_project.Title)}.scoreproj",
            Title = "프로젝트 저장"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            _project.Title = TitleInput.Text.Trim();
            ProjectFileService.Save(dialog.FileName, _project);
            RefreshTitle();
            StatusText.Content = $"저장 완료: {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ShowFileError("프로젝트를 저장할 수 없습니다.", "저장 오류", ex);
            StatusText.Content = "저장 실패";
        }
    }

    private void TitleInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_project is null || _updatingUi) return;
        _project.Title = TitleInput.Text;
        RefreshTitle();
        RefreshScore();
    }

    private void PageSizeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_project is null || _updatingUi) return;
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } }) return;

        var values = tag.Split('|');
        _project.PageSize = values[0];
        _project.Orientation = values[1];
        RefreshScore();
    }

    /// <summary>Shows every value of the current project in the window.</summary>
    private void ApplyProjectToUi()
    {
        _updatingUi = true;
        try
        {
            TitleInput.Text = _project.Title;

            // A page setting that is not in the list (e.g. from a newer file) is kept as is and shown as unselected.
            var tag = $"{_project.PageSize}|{_project.Orientation}";
            PageSizeInput.SelectedItem = PageSizeInput.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _updatingUi = false;
        }

        RefreshTitle();
        RefreshParts();
        RefreshScore();
        ImportStatusText.Text = string.IsNullOrWhiteSpace(_project.ImportedFrom)
            ? "MusicXML 가져오기 지원 · MUSX 미구현"
            : $"가져온 원본: {System.IO.Path.GetFileName(_project.ImportedFrom)}";
    }

    /// <summary>Lays the score out again and replaces the pages on screen.</summary>
    private void RefreshScore()
    {
        if (PagesPanel is null) return;

        var layout = ScoreLayoutEngine.Layout(_project);
        PagesPanel.Children.Clear();
        foreach (var page in layout.Pages)
        {
            PagesPanel.Children.Add(new Border
            {
                Child = new PageView(page),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xD2, 0xD9)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 24),
                SnapsToDevicePixels = true,
            });
        }

        var measureCount = _project.Parts.Count == 0 ? 0 : _project.Parts.Max(p => p.Measures.Count);
        ScoreInfoText.Content = MusicFont.Typeface is null
            ? $"악보 기호 글꼴({MusicFont.FileName})을 불러오지 못해 기호를 표시할 수 없습니다."
            : $"{layout.Pages.Count}쪽 · {measureCount}마디 · 음표 {_project.Parts.SelectMany(p => p.Measures).Sum(m => m.Events.Count(ev => ev.Kind == "Note"))}개";
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, ZoomLevels[0], ZoomLevels[^1]);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;

        _updatingUi = true;
        try
        {
            ZoomInput.SelectedItem = ZoomInput.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => Math.Abs(ParseZoomTag(item) - _zoom) < 0.001);
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void StepZoom(int direction)
    {
        var next = direction > 0
            ? ZoomLevels.FirstOrDefault(level => level > _zoom + 0.001, ZoomLevels[^1])
            : ZoomLevels.LastOrDefault(level => level < _zoom - 0.001, ZoomLevels[0]);
        SetZoom(next);
    }

    private static double ParseZoomTag(ComboBoxItem item) =>
        double.Parse((string)item.Tag, CultureInfo.InvariantCulture);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(+1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void ZoomInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || ZoomInput.SelectedItem is not ComboBoxItem item) return;
        SetZoom(ParseZoomTag(item));
    }

    private void ScoreScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        StepZoom(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                StepZoom(+1);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                StepZoom(-1);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                SetZoom(1);
                e.Handled = true;
                break;
        }
    }

    private void RefreshParts()
    {
        SidebarParts.Text = _project.Parts.Count == 0
            ? "파트 없음"
            : string.Join("\n", _project.Parts.Select((part, index) =>
                $"{index + 1}. {(string.IsNullOrWhiteSpace(part.Name) ? "(이름 없음)" : part.Name)} · {DescribeStaves(part)} · {part.Measures.Count}마디"));
    }

    private static string DescribeStaves(ScorePart part)
    {
        var clefs = string.Join("/", part.Staves.Select(staff => staff.Clef switch
        {
            ClefType.Treble => "높은음",
            ClefType.Treble8vb => "높은음 8vb",
            ClefType.Bass => "낮은음",
            ClefType.Alto => "알토",
            ClefType.Tenor => "테너",
            _ => staff.Clef.ToString(),
        }));
        return part.Staves.Count > 1 ? $"{part.Staves.Count}단({clefs})" : $"{clefs}자리표";
    }

    private void ShowFileError(string headline, string caption, Exception ex)
    {
        var detail = ex is ProjectFileException
            ? ex.Message
            : $"예상하지 못한 오류가 발생했습니다.\n\n{ex.GetType().Name}: {ex.Message}";
        MessageBox.Show(this, $"{headline}\n\n{detail}", caption, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void RefreshTitle()
    {
        if (SidebarTitle is not null) SidebarTitle.Text = _project.Title;
        if (TitleInput is not null && Title != null) Title = $"{_project.Title} — ScoreStudio";
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "ScoreStudio 개발 버전\n\n지원: 프로젝트 저장/열기, 악보 화면(오선·음자리표·조표·박자표·마디선·음표·쉼표, 여러 페이지, 확대/축소), MusicXML 가져오기\n저장되지만 아직 화면에 표시하지 않음: 가사, 코드 이름, 붙임줄, 도돌이표·볼타 (빔도 아직 그리지 않음)\n아직 지원하지 않음: 음표 입력, MUSX 변환, PDF 출력",
            "개발 상태", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "새 악보" : safe;
    }
}
