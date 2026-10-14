using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScoreStudio.Models;
using ScoreStudio.Rendering;

namespace ScoreStudio;

/// <summary>How the original page pictures are shown next to a recognised score (docs/OMR_COMPARE.md).</summary>
public enum CompareMode
{
    Off,
    SideBySide,
    Overlay,
}

// Comparing a recognised score with its original pages: side by side, or the original laid over the score.
public partial class MainWindow
{
    private static readonly Color OriginalInk = Color.FromRgb(0xD0, 0x1C, 0x1C);
    private static readonly Brush PageBorderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xCC, 0xD2, 0xD9)));

    private CompareMode _compareMode = CompareMode.Off;

    /// <summary>The width the original column had before it was hidden (the splitter may have changed it).</summary>
    private GridLength _originalWidth = new(1, GridUnitType.Star);

    /// <summary>The upper layer of each overlaid page: the one the transparency applies to.</summary>
    private readonly List<UIElement> _overlayTops = new();

    private bool _syncingScroll;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private IEnumerable<ScoreSourcePage> SourcesOf(int page) => Project.SourcePages.Where(s => s.Page == page);

    /// <summary>The compare tools work only for a score with original pictures; without them comparing is off.</summary>
    private void RefreshCompareUi()
    {
        var has = Project.SourcePages.Count > 0;
        CompareToolBar.IsEnabled = has;
        CompareMenu.IsEnabled = has;
        if (!has && _compareMode != CompareMode.Off) SetCompareMode(CompareMode.Off);
        else ShowCompareMode();
    }

    private void SetCompareMode(CompareMode mode)
    {
        if (mode != CompareMode.Off && Project.SourcePages.Count == 0) mode = CompareMode.Off;
        if (_compareMode == CompareMode.SideBySide && OriginalColumn.Width.Value > 0) _originalWidth = OriginalColumn.Width;
        _compareMode = mode;
        var side = mode == CompareMode.SideBySide;
        OriginalColumn.Width = side ? _originalWidth : new GridLength(0);
        SplitterColumn.Width = new GridLength(side ? 6 : 0);
        ShowCompareMode();
        BuildPageVisuals();
        RefreshMarks();
        if (side) Dispatcher.BeginInvoke(new Action(SyncOriginalToScore), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Shows the mode in the tool bar and the menu, and enables only the options that mode uses.</summary>
    private void ShowCompareMode()
    {
        var tag = _compareMode.ToString();
        _updatingUi = true;
        try
        {
            CompareModeInput.SelectedItem = CompareModeInput.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        }
        finally
        {
            _updatingUi = false;
        }

        foreach (var item in CompareMenu.Items.OfType<MenuItem>()) item.IsChecked = (item.Tag as string) == tag;
        SyncScrollInput.IsEnabled = _compareMode == CompareMode.SideBySide;
        OriginalOnTopInput.IsEnabled = _compareMode == CompareMode.Overlay;
        OverlayTransparencyInput.IsEnabled = _compareMode == CompareMode.Overlay;
    }

    /// <summary>Puts the score's pages on screen (with the original laid over them when overlaying) and the original column's pages.</summary>
    private void BuildPageVisuals()
    {
        if (PagesPanel is null) return;
        const double k = PageRenderer.DipPerMm;
        PagesPanel.Children.Clear();
        OriginalPanel.Children.Clear();
        _overlayTops.Clear();

        for (var p = 0; p < _pageViews.Count; p++)
        {
            var view = _pageViews[p];
            Detach(view);
            UIElement content = view;
            var sources = SourcesOf(p).ToList();
            if (_compareMode == CompareMode.Overlay && sources.Count > 0)
            {
                var layers = new Grid { Width = view.Width, Height = view.Height };
                var originals = sources.Select(s => Picture(s, SourcePageImages.Ink(s.Png, OriginalInk))).ToList();
                if (OriginalOnTopInput.IsChecked == true)
                {
                    layers.Children.Add(view);
                    foreach (var original in originals)
                    {
                        layers.Children.Add(original);
                        _overlayTops.Add(original);
                    }
                }
                else
                {
                    // The original goes on a white page so the score above, made see-through, shows it.
                    layers.Children.Add(new Border { Background = Brushes.White, IsHitTestVisible = false });
                    foreach (var original in originals) layers.Children.Add(original);
                    layers.Children.Add(view);
                    _overlayTops.Add(view);
                }

                content = layers;
            }

            PagesPanel.Children.Add(PageFrame(content));

            if (_compareMode == CompareMode.SideBySide)
            {
                var page = new Grid { Width = view.Width, Height = view.Height, Background = Brushes.White };
                foreach (var source in sources) page.Children.Add(Picture(source, SourcePageImages.Plain(source.Png)));
                if (sources.Count == 0)
                {
                    page.Children.Add(new TextBlock
                    {
                        Text = "이 쪽의 원본 없음", Foreground = Brushes.Gray, FontSize = 16,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                OriginalPanel.Children.Add(PageFrame(page));
            }
        }

        ApplyOverlayTransparency();
        return;

        static Image Picture(ScoreSourcePage source, ImageSource image)
        {
            var picture = new Image
            {
                Source = image, Stretch = Stretch.Fill, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(source.Left * k, source.Top * k, 0, 0), Width = source.Width * k, Height = source.Height * k,
            };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return picture;
        }

        // A page kept across a change of mode still sits in its old frame or layers.
        static void Detach(PageView view)
        {
            switch (VisualTreeHelper.GetParent(view))
            {
                case Border border: border.Child = null; break;
                case Panel panel: panel.Children.Remove(view); break;
            }
        }

        static Border PageFrame(UIElement content) => new()
        {
            Child = content, BorderBrush = PageBorderBrush, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 24), SnapsToDevicePixels = true,
        };
    }

    /// <summary>The upper layer of an overlay is as see-through as the slider says.</summary>
    private void ApplyOverlayTransparency()
    {
        var opacity = 1 - OverlayTransparencyInput.Value;
        foreach (var top in _overlayTops) top.Opacity = opacity;
        foreach (var view in _pageViews.Where(v => !_overlayTops.Contains(v))) view.Opacity = 1;
    }

    private void CompareMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Also raised while the window is being built, before the columns exist.
        if (_updatingUi || PagesPanel is null || CompareModeInput.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        SetCompareMode(Enum.Parse<CompareMode>(tag));
        ScoreScroll.Focus();
    }

    private void CompareMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag }) SetCompareMode(Enum.Parse<CompareMode>(tag));
    }

    private void CompareOption_Changed(object sender, RoutedEventArgs e)
    {
        if (sender == OriginalOnTopInput && _compareMode == CompareMode.Overlay)
        {
            BuildPageVisuals();
            RefreshMarks();
        }

        if (sender == SyncScrollInput && SyncScrollInput.IsChecked == true) SyncOriginalToScore();
        ScoreScroll.Focus();
    }

    private void OverlayTransparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PagesPanel is null) return;
        ApplyOverlayTransparency();
    }

    private bool SyncingScrolls => _compareMode == CompareMode.SideBySide && SyncScrollInput.IsChecked == true && !_syncingScroll;

    // One side leads at a time: the original while the pointer is over it (its wheel or scroll bar), the score
    // otherwise. Following both ways would pull the leader back when the two cannot scroll equally far (a scroll bar
    // across one of them makes it a little shorter).
    private void ScoreScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (SyncingScrolls && !OriginalScroll.IsMouseOver && (e.VerticalChange != 0 || e.HorizontalChange != 0 || e.ExtentHeightChange != 0))
            FollowScroll(ScoreScroll, OriginalScroll);
    }

    private void OriginalScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (SyncingScrolls && OriginalScroll.IsMouseOver && (e.VerticalChange != 0 || e.HorizontalChange != 0))
            FollowScroll(OriginalScroll, ScoreScroll);
    }

    private void SyncOriginalToScore()
    {
        if (SyncingScrolls) FollowScroll(ScoreScroll, OriginalScroll);
    }

    /// <summary>
    /// Moves <paramref name="follower"/> to where <paramref name="leader"/> is: both columns hold pages of the same size
    /// and gaps, so the same vertical offset shows the same place; across, the same share of the scrollable width.
    /// </summary>
    private void FollowScroll(ScrollViewer leader, ScrollViewer follower)
    {
        _syncingScroll = true;
        try
        {
            follower.ScrollToVerticalOffset(leader.VerticalOffset);
            if (leader.ScrollableWidth > 0 && follower.ScrollableWidth > 0)
                follower.ScrollToHorizontalOffset(leader.HorizontalOffset / leader.ScrollableWidth * follower.ScrollableWidth);
        }
        finally
        {
            _syncingScroll = false;
        }
    }
}
