using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScoreStudio.Models;
using ScoreStudio.Services;

namespace ScoreStudio;

public partial class MainWindow : Window
{
    private ScoreProject _project = ScoreProject.CreateNew();

    public MainWindow()
    {
        InitializeComponent();
        RenderStaffPlaceholder();
        RefreshTitle();
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
        TitleInput.Text = _project.Title;
        RefreshTitle();
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
            TitleInput.Text = _project.Title;
            RefreshTitle();
            StatusText.Content = $"열기 완료: {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트를 열 수 없습니다.\n\n{ex.Message}", "열기 오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, $"프로젝트를 저장할 수 없습니다.\n\n{ex.Message}", "저장 오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TitleInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_project is null) return;
        _project.Title = TitleInput.Text;
        RefreshTitle();
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
