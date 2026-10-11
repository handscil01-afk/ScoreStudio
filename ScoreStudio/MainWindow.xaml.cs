using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ScoreStudio.Editing;
using ScoreStudio.Import;
using ScoreStudio.KeyInput;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Rendering;
using ScoreStudio.Services;

namespace ScoreStudio;

public partial class MainWindow : Window
{
    /// <summary>Where the program's own settings live: the user's application data folder.</summary>
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScoreStudio", "settings.json");

    private readonly AppSettings _settings = AppSettings.Load(SettingsPath);
    private ScoreEditor _editor = new(ScoreProject.CreateNew());
    private ScoreLayout? _layout;
    private readonly List<PageView> _pageViews = new();
    private int _savedChangeCount;
    private bool _updatingUi;

    /// <summary>Mouse entry: a click on a staff enters a note at that height instead of selecting.</summary>
    private bool _mouseEntry;

    /// <summary>The last pointer place over a page (page index and mm), to redraw the entry preview after an edit.</summary>
    private (int Page, double X, double Y)? _pointer;

    private static readonly double[] ZoomLevels = { 0.5, 0.75, 1, 1.25, 1.5, 2, 3 };

    private double _zoom = 1;

    private ScoreProject Project => _editor.Project;

    /// <summary>Edits since the last save, new score or open (an undo back to the saved state still counts).</summary>
    private bool HasUnsavedEdits => _editor.ChangeCount != _savedChangeCount;

    public MainWindow()
    {
        InitializeComponent();
        _editor.TieAcrossBarlines = _settings.TieAcrossBarlines;
        TieAcrossBarlinesMenuItem.IsChecked = _settings.TieAcrossBarlines;
        BuildSignatureMenus();
        SetZoom(1);
        ApplyProjectToUi();
        Loaded += (_, _) => ScoreScroll.Focus();
    }

    private void UseProject(ScoreProject project)
    {
        _editor = new ScoreEditor(project) { TieAcrossBarlines = _settings.TieAcrossBarlines };
        _savedChangeCount = _editor.ChangeCount;
        ApplyProjectToUi();
    }

    /// <summary>Asks before unsaved edits are thrown away; true when it is fine to go on.</summary>
    private bool ConfirmDiscard()
    {
        if (!HasUnsavedEdits) return true;
        return MessageBox.Show(this, "저장하지 않은 편집이 있습니다. 편집한 내용을 버리고 계속할까요?", "저장하지 않은 편집",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    private void NewScore_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        UseProject(ScoreProject.CreateNew());
        StatusText.Content = "새 악보를 만들었습니다. 음 이름(C~B) 키로 입력하세요.";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog
        {
            Filter = "ScoreStudio 프로젝트 (*.scoreproj)|*.scoreproj|JSON 파일 (*.json)|*.json",
            Title = "프로젝트 열기"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            UseProject(ProjectFileService.Load(dialog.FileName));
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
        if (!ConfirmDiscard()) return;
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

        UseProject(result.Project);
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
            FileName = $"{SafeFileName(Project.Title)}.scoreproj",
            Title = "프로젝트 저장"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Project.Title = TitleInput.Text.Trim();
            ProjectFileService.Save(dialog.FileName, Project);
            _savedChangeCount = _editor.ChangeCount;
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
        if (_updatingUi) return;
        Project.Title = TitleInput.Text;
        RefreshTitle();
        RefreshScore();
    }

    private void PageSizeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } }) return;

        var values = tag.Split('|');
        Project.PageSize = values[0];
        Project.Orientation = values[1];
        RefreshScore();
    }

    // ---- Editing -------------------------------------------------------------------------------------------------

    /// <summary>Shows the outcome of an editor command: redraws when the score changed, otherwise only the marks.</summary>
    private void ShowEdit(EditResult result, int changesBefore)
    {
        StatusText.Content = result.Done ? result.Message : $"할 수 없음: {result.Message}";
        if (_editor.ChangeCount != changesBefore)
        {
            RefreshParts();
            RefreshScore();
        }
        else
        {
            RefreshMarks();
        }

        RefreshEditState();
        BringCursorIntoView();
    }

    private void RunEdit(Func<ScoreEditor, EditResult> command)
    {
        var before = _editor.ChangeCount;
        ShowEdit(command(_editor), before);
        ScoreScroll.Focus();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => RunEdit(ed => ed.Undo());

    private void Redo_Click(object sender, RoutedEventArgs e) => RunEdit(ed => ed.Redo());

    private void CopyMeasure_Click(object sender, RoutedEventArgs e) => RunEdit(ed => ed.CopyMeasure());

    private void PasteMeasure_Click(object sender, RoutedEventArgs e) => RunEdit(ed => ed.PasteMeasure());

    private void InsertMeasure_Click(object sender, RoutedEventArgs e) => RunEdit(ed => ed.InsertMeasureAfter());

    private void DeleteMeasure_Click(object sender, RoutedEventArgs e) => RunEdit(ed => ed.DeleteMeasure());

    /// <summary>Time signatures offered in the edit menu.</summary>
    private static readonly (int Numerator, int Denominator)[] TimeSignatures =
    {
        (2, 4), (3, 4), (4, 4), (5, 4), (6, 4), (2, 2), (3, 2), (3, 8), (5, 8), (6, 8), (7, 8), (9, 8), (12, 8),
    };

    /// <summary>Fills the key, time signature and clef submenus of the edit menu.</summary>
    private void BuildSignatureMenus()
    {
        for (var fifths = -7; fifths <= 7; fifths++)
        {
            var value = fifths;
            KeyMenu.Items.Add(CommandItem(ScoreEditor.KeyName(value), () => RunEdit(ed => ed.SetKey(value))));
        }

        foreach (var (numerator, denominator) in TimeSignatures)
            TimeMenu.Items.Add(CommandItem($"{numerator}/{denominator}", () => RunEdit(ed => ed.SetTime(numerator, denominator))));

        foreach (var clef in Enum.GetValues<ClefType>())
            ClefMenu.Items.Add(CommandItem(ScoreEditor.ClefName(clef), () => RunEdit(ed => ed.SetClef(clef))));
    }

    private static MenuItem CommandItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void Rest_Click(object sender, RoutedEventArgs e) => RunEdit(ed => ed.EnterRest());

    private void Dot_Click(object sender, RoutedEventArgs e) => RunEdit(ed => ed.ToggleDot());

    private void Duration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<NoteType>(tag, out var type))
            RunEdit(ed => ed.SetInputValue(type, 0));
    }

    private void TieAcrossBarlines_Click(object sender, RoutedEventArgs e)
    {
        _settings.TieAcrossBarlines = TieAcrossBarlinesMenuItem.IsChecked;
        _editor.TieAcrossBarlines = _settings.TieAcrossBarlines;
        var saved = _settings.Save(SettingsPath);
        StatusText.Content = (_settings.TieAcrossBarlines
                ? "마디를 넘는 음표는 다음 마디로 붙임줄로 이어 넣습니다."
                : "마디에 남은 길이보다 긴 음표는 넣지 않습니다.")
            + (saved ? "" : " (설정 파일을 저장하지 못해 프로그램을 닫으면 원래대로 돌아갑니다.)");
        ScoreScroll.Focus();
    }

    private void KeyHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "악보를 한 번 누른 뒤 키보드로 입력합니다.\n\n" +
            "C D E F G A B : 그 음 입력 (조표를 따르고, 앞 음에 가까운 옥타브)\n" +
            "Shift + 음 이름 : 선택한 화음에 음 더하기\n" +
            "0 : 쉼표 입력\n" +
            "7 6 5 4 3 2 : 온·2분·4분·8분·16분·32분음표 (Finale Speedy Entry와 같은 번호)\n" +
            ". : 점 붙이기·떼기\n" +
            "↑ ↓ : 반음 올리기·내리기, Ctrl + ↑ ↓ : 옥타브\n" +
            "← → : 이전·다음 음표 선택\n" +
            "Delete, Backspace : 선택한 음표를 쉼표로\n" +
            "Ctrl+Z, Ctrl+Y : 실행 취소, 다시 실행\n" +
            "Ctrl+C, Ctrl+V : 입력 위치의 마디 복사, 붙여넣기\n" +
            "Esc : 선택 해제\n\n" +
            "N : 마우스 입력 켜기·끄기 (켜면 오선 위를 눌러 그 높이의 음표를 넣고, Shift+누르기로 화음에 더함)\n\n" +
            "음표를 누르면 선택하고, 빈 곳을 누르면 그 마디에 입력 위치를 둡니다.\n" +
            "음표를 선택한 상태에서 길이 키를 누르면 그 음표의 길이가 바뀝니다.\n" +
            "마디에 남은 길이보다 긴 음표는 넣지 않습니다. 설정 메뉴에서 다음 마디로 붙임줄로 이어 넣게 바꿀 수 있습니다.\n\n" +
            "조표·박자표는 편집 메뉴에서 입력 위치의 마디부터 다음 변경 전까지 바꾸고, 음자리표는 선택한 음표(없으면 입력 위치)에 넣습니다.",
            "키보드 입력 방법", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MouseEntry_Click(object sender, RoutedEventArgs e) => SetMouseEntry(MouseEntryButton.IsChecked == true);

    private void SetMouseEntry(bool on)
    {
        _mouseEntry = on;
        MouseEntryButton.IsChecked = on;
        if (on) ScoreScroll.Cursor = Cursors.Cross;
        else ScoreScroll.ClearValue(CursorProperty);
        StatusText.Content = on
            ? "마우스 입력: 오선 위를 누르면 그 높이에 음표를 넣습니다(Shift: 화음에 더하기). N 또는 Esc로 끕니다."
            : "마우스 입력을 껐습니다. 누르면 음표를 선택합니다.";
        UpdatePreview(describe: false);
        ScoreScroll.Focus();
    }

    private void Score_MouseMove(object sender, MouseEventArgs e)
    {
        _pointer = null;
        if (e.OriginalSource is DependencyObject source && FindPageView(source) is { } view)
        {
            var point = e.GetPosition(view);
            _pointer = (_pageViews.IndexOf(view), point.X / PageRenderer.DipPerMm, point.Y / PageRenderer.DipPerMm);
        }

        UpdatePreview();
    }

    private void Score_MouseLeave(object sender, MouseEventArgs e)
    {
        _pointer = null;
        UpdatePreview();
    }

    /// <summary>
    /// In mouse entry, the grey note where a click would enter one; nothing otherwise. <paramref name="describe"/> also
    /// names it in the status bar (not right after an edit, whose message stays).
    /// </summary>
    private void UpdatePreview(bool describe = true)
    {
        var target = _mouseEntry && _layout is not null && _pointer is { } p
            ? MouseEntry.Find(_layout, p.Page, Project, _editor.InputType, p.X, p.Y)
            : null;
        for (var i = 0; i < _pageViews.Count; i++)
            _pageViews[i].SetPreview(target is not null && i == _pointer!.Value.Page ? target.Preview : null);
        if (target is not null && describe)
            StatusText.Content = $"{target.PitchName} · {Project.Parts[target.At.Part].Measures[target.At.Measure].Number}마디 {(target.At.Position.ToDouble() + 1).ToString("0.##", CultureInfo.InvariantCulture)}박에 넣기";
    }

    private void Score_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ScoreScroll.Focus();
        if (_layout is null || e.OriginalSource is not DependencyObject source) return;
        var view = FindPageView(source);
        if (view is null) return;

        var point = e.GetPosition(view);
        var (x, y) = (point.X / PageRenderer.DipPerMm, point.Y / PageRenderer.DipPerMm);
        var sp = _layout.StaffSpace;
        var before = _editor.ChangeCount;
        if (_mouseEntry)
        {
            if (MouseEntry.Find(_layout, _pageViews.IndexOf(view), Project, _editor.InputType, x, y) is { } target)
            {
                var add = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                ShowEdit(add ? _editor.AddToChordAt(target.At, target.Diatonic) : _editor.EnterNoteAt(target.At, target.Diatonic), before);
                UpdatePreview(describe: false);
            }

            e.Handled = true;
            return;
        }

        if (LayoutHitTest.FindChord(view.Page, x, y, 0.4 * sp) is { } chord)
        {
            ShowEdit(_editor.Select(new EditPosition(chord.PartIndex, chord.Staff, chord.Voice, chord.MeasureIndex, chord.Start)), before);
        }
        else if (LayoutHitTest.FindStaffMeasure(view.Page, x, y, sp) is { } hit)
        {
            // The cursor goes before the nearest chord of the staff in that measure to the right of the click, else to
            // the end of what is already entered (or the measure's start).
            var boxes = view.Page.Chords.Where(c => c.PartIndex == hit.PartIndex && c.Staff == hit.Staff && c.MeasureIndex == hit.MeasureIndex && c.Voice == 1)
                .OrderBy(c => c.Start).ToList();
            var position = boxes.FirstOrDefault(c => c.Right >= x)?.Start ?? EndOfVoice(hit);
            ShowEdit(_editor.PlaceCursor(hit.PartIndex, hit.Staff, hit.MeasureIndex, position), before);
        }

        e.Handled = true;
    }

    /// <summary>Where voice 1 of a staff ends in a measure: after its last chord, or the start of an empty measure.</summary>
    private Rational EndOfVoice(StaffMeasureHit hit)
    {
        var measure = Project.Parts[hit.PartIndex].Measures[hit.MeasureIndex];
        var end = MouseEntry.EndOfVoice(measure, hit.Staff);
        return end < ScoreEditor.MeasureLength(measure) ? end : Rational.Zero;
    }

    private static PageView? FindPageView(DependencyObject? node)
    {
        while (node is not null and not PageView) node = VisualTreeHelper.GetParent(node);
        return node as PageView;
    }

    // ---- Showing the project -------------------------------------------------------------------------------------

    /// <summary>Shows every value of the current project in the window.</summary>
    private void ApplyProjectToUi()
    {
        _updatingUi = true;
        try
        {
            TitleInput.Text = Project.Title;

            // A page setting that is not in the list (e.g. from a newer file) is kept as is and shown as unselected.
            var tag = $"{Project.PageSize}|{Project.Orientation}";
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
        RefreshEditState();
        ImportStatusText.Text = string.IsNullOrWhiteSpace(Project.ImportedFrom)
            ? "MusicXML 가져오기 지원 · MUSX 미구현"
            : $"가져온 원본: {System.IO.Path.GetFileName(Project.ImportedFrom)}";
    }

    /// <summary>Lays the score out again and replaces the pages on screen.</summary>
    private void RefreshScore()
    {
        if (PagesPanel is null) return;

        _layout = ScoreLayoutEngine.Layout(Project);
        PagesPanel.Children.Clear();
        _pageViews.Clear();
        foreach (var page in _layout.Pages)
        {
            var view = new PageView(page);
            _pageViews.Add(view);
            PagesPanel.Children.Add(new Border
            {
                Child = view,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xD2, 0xD9)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 24),
                SnapsToDevicePixels = true,
            });
        }

        RefreshMarks();
        RefreshInfo();
    }

    /// <summary>Moves the selection box and the cursor bar to where the editor has them.</summary>
    private void RefreshMarks()
    {
        if (_layout is null) return;
        var marks = EditMarks.Find(_layout, _editor.Selection, _editor.Cursor);
        for (var p = 0; p < _pageViews.Count; p++)
        {
            _pageViews[p].SetMarks(
                p == marks.SelectionPage ? marks.Selection : null,
                p == marks.CursorPage ? (marks.CursorX, marks.CursorTop, marks.CursorBottom) : null);
        }

        RefreshInfo();
    }

    private void BringCursorIntoView()
    {
        if (_layout is null) return;
        var marks = EditMarks.Find(_layout, _editor.Selection, _editor.Cursor);
        if (marks.CursorPage < 0 || marks.CursorPage >= _pageViews.Count) return;
        const double k = PageRenderer.DipPerMm;
        _pageViews[marks.CursorPage].BringIntoView(new Rect((marks.CursorX - 20) * k, (marks.CursorTop - 10) * k, 40 * k, (marks.CursorBottom - marks.CursorTop + 20) * k));
    }

    private void RefreshEditState()
    {
        UndoMenuItem.IsEnabled = _editor.CanUndo;
        RedoMenuItem.IsEnabled = _editor.CanRedo;
        PasteMenuItem.IsEnabled = _editor.CanPaste;
        InputValueText.Text = ScoreEditor.DescribeValue(_editor.InputType, _editor.InputDots, rest: false);
        foreach (var button in InputToolBar.Items.OfType<System.Windows.Controls.Primitives.ToggleButton>())
        {
            button.IsChecked = button == DotButton ? _editor.InputDots > 0 : (button.Tag as string) == _editor.InputType.ToString();
        }
        RefreshTitle();
    }

    private void RefreshInfo()
    {
        if (_layout is null || ScoreInfoText is null) return;
        var measureCount = Project.Parts.Count == 0 ? 0 : Project.Parts.Max(p => p.Measures.Count);
        var cursor = _editor.Cursor;
        var where = cursor.Part < Project.Parts.Count && cursor.Measure < Project.Parts[cursor.Part].Measures.Count
            ? $" · 입력 위치: {Project.Parts[cursor.Part].Measures[cursor.Measure].Number}마디 {(cursor.Position.ToDouble() + 1).ToString("0.##", CultureInfo.InvariantCulture)}박"
            : "";
        ScoreInfoText.Content = MusicFont.Typeface is null
            ? $"악보 기호 글꼴({MusicFont.FileName})을 불러오지 못해 기호를 표시할 수 없습니다."
            : $"{_layout.Pages.Count}쪽 · {measureCount}마디 · 음표 {Project.Parts.SelectMany(p => p.Measures).Sum(m => m.Events.Count(ev => ev.Kind == "Note"))}개{where}";
    }

    // ---- Zoom ----------------------------------------------------------------------------------------------------

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
        // Typing in the title box or a list stays there; everywhere else keys edit the score.
        if (Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem))
        {
            if (Keyboard.Modifiers == ModifierKeys.None && (e.Key == Key.N || (e.Key == Key.Escape && _mouseEntry)))
            {
                SetMouseEntry(e.Key == Key.N && !_mouseEntry);
                e.Handled = true;
                return;
            }

            var before = _editor.ChangeCount;
            if (EditorKeys.Handle(_editor, e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers) is { } result)
            {
                ShowEdit(result, before);
                e.Handled = true;
                return;
            }
        }

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

    // ---- Side panels ---------------------------------------------------------------------------------------------

    private void RefreshParts()
    {
        SidebarParts.Text = Project.Parts.Count == 0
            ? "파트 없음"
            : string.Join("\n", Project.Parts.Select((part, index) =>
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
        if (SidebarTitle is not null) SidebarTitle.Text = Project.Title;
        Title = $"{(HasUnsavedEdits ? "* " : "")}{Project.Title} — ScoreStudio";
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "ScoreStudio 개발 버전\n\n지원: 프로젝트 저장/열기, 악보 화면(오선·음자리표·조표·박자표·마디선·음표·쉼표·빔·붙임줄·잇단음표·가사·코드 이름·도돌이표·볼타·음자리표 변경·옥타브 기호, 여러 페이지, 확대/축소), MusicXML 가져오기, 키보드와 마우스로 음표·쉼표 입력과 고치기, 조표·박자표·음자리표 바꾸기(실행 취소 포함)\n아직 가져오지 않음: 셈여림, 이음줄, 아티큘레이션, 글자 지시(가져오기 결과 창에 경고로 표시)\n아직 지원하지 않음: 잇단음표·가사 입력, MUSX 변환, PDF 출력",
            "개발 상태", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "새 악보" : safe;
    }
}
