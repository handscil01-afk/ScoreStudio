using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio;

public partial class MainWindow : Window
{
    private ScoreProject _project = ScoreProject.CreateNew();
    private bool _updatingUi;

    public MainWindow()
    {
        InitializeComponent();
        RenderStaffPlaceholder();
        ApplyProjectToUi();
    }

    private void RenderStaffPlaceholder()
    {
        ScoreCanvas.Children.Clear();
        const double left = 12;
        const double right = 575;
        const double top = 80;
        const double gap = 10;

        for (int line = 0; line < 5; line++)
        {
            var shape = new System.Windows.Shapes.Line
            {
                X1 = left,
                X2 = right,
                Y1 = top + line * gap,
                Y2 = top + line * gap,
                Stroke = System.Windows.Media.Brushes.Black,
                StrokeThickness = 1
            };
            ScoreCanvas.Children.Add(shape);
        }

        var notice = new TextBlock
        {
            Text = "오선 미리보기 — 채보 기능은 개발 예정",
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Gray
        };
        Canvas.SetLeft(notice, left);
        Canvas.SetTop(notice, top + 70);
        ScoreCanvas.Children.Add(notice);
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
    }

    private void PageSizeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_project is null || _updatingUi) return;
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } }) return;

        var values = tag.Split('|');
        _project.PageSize = values[0];
        _project.Orientation = values[1];
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
    }

    private void RefreshParts()
    {
        SidebarParts.Text = _project.Parts.Count == 0
            ? "파트 없음"
            : string.Join("\n", _project.Parts.Select((part, index) =>
                $"{index + 1}. {(string.IsNullOrWhiteSpace(part.Name) ? "(이름 없음)" : part.Name)} · {part.Measures.Count}마디"));
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
        if (ScoreTitle is not null) ScoreTitle.Text = string.IsNullOrWhiteSpace(_project.Title) ? "제목 없는 악보" : _project.Title;
        if (SidebarTitle is not null) SidebarTitle.Text = _project.Title;
        if (TitleInput is not null && Title != null) Title = $"{_project.Title} — ScoreStudio";
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "ScoreStudio 초기 개발 킷\n\n현재는 WPF 화면 뼈대와 자체 프로젝트 JSON 저장/열기를 포함합니다.\nMUSX 가져오기, 음표 편집, PDF 출력은 아직 구현되지 않았습니다.",
            "개발 상태", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "새 악보" : safe;
    }
}
