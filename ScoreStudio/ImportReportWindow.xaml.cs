using System.Windows;
using ScoreStudio.Import;

namespace ScoreStudio;

/// <summary>Shows what an import brought in and everything it skipped, so nothing is dropped silently.</summary>
public partial class ImportReportWindow : Window
{
    private readonly ImportResult _result;

    /// <param name="notes">More to tell about the result (music recognition: overlaps removed, pages not read, …).</param>
    public ImportReportWindow(string fileName, ImportResult result, IReadOnlyList<string>? notes = null)
    {
        InitializeComponent();
        _result = result;
        if (notes is { Count: > 0 })
        {
            NotesText.Text = string.Join(Environment.NewLine, notes);
            NotesText.Visibility = Visibility.Visible;
        }

        var summary = result.Summary;
        FileText.Text = $"가져오기 완료: {fileName}";
        SummaryText.Text = $"파트 {summary.Parts}개 · 마디 {summary.Measures}개 · 음표 {summary.Notes}개 · 쉼표 {summary.Rests}개\n"
            + $"가사 {summary.Lyrics}개({summary.LyricVerses}절까지) · 붙임줄 {summary.Ties}개 · 코드 이름 {summary.ChordSymbols}개";
        WarningHeader.Text = result.Warnings.Count == 0
            ? "경고 없음: 이 파일의 모든 요소를 가져왔습니다."
            : $"경고 {result.Warnings.Count}종류 (총 {result.Warnings.Sum(w => w.Count)}개)";
        WarningList.ItemsSource = result.Warnings;
        WarningList.Visibility = result.Warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var lines = _result.Warnings.Select(w => $"{w.Message}\t{w.Count}\t{w.FirstLocation}");
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, new[] { FileText.Text, SummaryText.Text, NotesText.Text }.Where(t => t.Length > 0).Concat(lines)));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program is holding the clipboard; copying is a convenience, so ignore it.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
