using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ScoreStudio.Editing;

namespace ScoreStudio.KeyInput;

/// <summary>
/// Edit > 단축키 설정 (docs/KEYS_AND_TOOLS.md): every command with its keys by scope; keys are added by pressing them,
/// removed, or put back to Finale's defaults. Works on a copy; <see cref="Result"/> is set when the user confirms.
/// </summary>
public sealed class KeySettingsWindow : Window
{
    private readonly KeyMap _map;
    private readonly ListView _list = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _search = new() { MinWidth = 220 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkSlateBlue, Margin = new Thickness(0, 0, 0, 8) };
    private Row? _capturing;

    /// <summary>One line of the table.</summary>
    public sealed record Row(KeyScope Scope, EditorCommand Command, string ScopeName, string Group, string Name, string Keys);

    public KeySettingsWindow(KeyMap current)
    {
        _map = KeyMap.WithChanges(current.Changes());
        Title = "단축키 설정";
        Width = 760;
        Height = 620;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var view = new GridView();
        foreach (var (header, path, width) in new[] { ("범위", nameof(Row.ScopeName), 110.0), ("묶음", nameof(Row.Group), 80.0), ("명령", nameof(Row.Name), 290.0), ("키", nameof(Row.Keys), 220.0) })
            view.Columns.Add(new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(path) });
        _list.View = view;
        _list.SelectionMode = SelectionMode.Single;

        var top = new DockPanel();
        var searchLabel = new TextBlock { Text = "찾기", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(searchLabel, Dock.Left);
        top.Children.Add(searchLabel);
        top.Children.Add(_search);
        _search.TextChanged += (_, _) => Refresh();

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        actions.Children.Add(MakeButton("키 더하기", StartCapture));
        actions.Children.Add(MakeButton("키 모두 빼기", RemoveKeys));
        actions.Children.Add(MakeButton("이 명령 기본값", ResetCommand));
        actions.Children.Add(MakeButton("모두 Finale 기본값", ResetAll));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = MakeButton("확인", () =>
        {
            Result = _map;
            DialogResult = true;
        });
        ok.IsDefault = false;
        var cancel = MakeButton("취소", () => DialogResult = false);
        cancel.IsCancel = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new DockPanel { Margin = new Thickness(14) };
        foreach (var (element, dock) in new (UIElement, Dock)[] { (top, Dock.Top), (_status, Dock.Top), (actions, Dock.Top), (buttons, Dock.Bottom) })
        {
            DockPanel.SetDock(element, dock);
            panel.Children.Add(element);
        }

        panel.Children.Add(_list);
        Content = panel;
        _status.Text = "명령을 고르고 '키 더하기'를 누른 뒤 새 키를 누르세요. 도구(Speedy·Simple Entry)의 키가 공통 키보다 먼저 쓰입니다.";
        PreviewKeyDown += CaptureKey;
        Refresh();
    }

    /// <summary>The changed key map when the user confirmed.</summary>
    public KeyMap? Result { get; private set; }

    private static Button MakeButton(string text, Action run)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => run();
        return button;
    }

    private IEnumerable<Row> Rows()
    {
        foreach (var scope in new[] { KeyScope.Speedy, KeyScope.Simple, KeyScope.Common })
        {
            foreach (var info in KeyMap.Commands)
            {
                var keys = _map.KeysFor(scope, info.Command);
                if (keys.Count == 0 && KeyMap.Defaults().KeysFor(scope, info.Command).Count == 0 && scope != KeyScope.Common) continue;
                var shown = info.Supported ? string.Join(", ", keys.Select(k => k.Display())) : "아직 지원하지 않음";
                yield return new Row(scope, info.Command, EditorTools.ScopeName(scope), info.Group, info.Name, shown);
            }
        }
    }

    private void Refresh(Row? keep = null)
    {
        var filter = _search.Text.Trim();
        var rows = Rows().Where(r => filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || r.Keys.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Group.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = rows;
        if (keep is not null) _list.SelectedItem = rows.FirstOrDefault(r => r.Scope == keep.Scope && r.Command == keep.Command);
    }

    private Row? Selected()
    {
        if (_list.SelectedItem is Row row) return row;
        _status.Text = "먼저 표에서 명령을 고르세요.";
        return null;
    }

    private void StartCapture()
    {
        if (Selected() is not { } row) return;
        if (!KeyMap.Info(row.Command).Supported)
        {
            _status.Text = "아직 지원하지 않는 명령에는 키를 줄 수 없습니다.";
            return;
        }

        _capturing = row;
        _status.Text = $"'{row.Name}'({row.ScopeName})에 줄 키를 누르세요. 취소하려면 Esc를 누르세요.";
    }

    private void CaptureKey(object sender, KeyEventArgs e)
    {
        if (_capturing is not { } row) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (EditorKeys.Stroke(key, Keyboard.Modifiers) is not { } stroke) return; // a modifier on its own: wait for the key
        e.Handled = true;
        _capturing = null;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            _status.Text = "키 더하기를 취소했습니다.";
            return;
        }

        var previous = _map.Add(row.Scope, row.Command, stroke);
        _status.Text = previous is { } other
            ? $"{stroke.Display()}를 '{row.Name}'에 주고, 같은 키를 쓰던 '{KeyMap.Info(other).Name}'에서는 뺐습니다."
            : $"{stroke.Display()}를 '{row.Name}'에 주었습니다.";
        Refresh(row);
    }

    private void RemoveKeys()
    {
        if (Selected() is not { } row) return;
        foreach (var key in _map.KeysFor(row.Scope, row.Command).ToList()) _map.Remove(row.Scope, row.Command, key);
        _status.Text = $"'{row.Name}'의 키를 모두 뺐습니다.";
        Refresh(row);
    }

    private void ResetCommand()
    {
        if (Selected() is not { } row) return;
        _map.Reset(row.Scope, row.Command);
        _status.Text = $"'{row.Name}'의 키를 기본값으로 돌렸습니다.";
        Refresh(row);
    }

    private void ResetAll()
    {
        foreach (var (scope, command, _) in _map.All().ToList()) _map.Reset(scope, command);
        foreach (var (scope, command, _) in KeyMap.Defaults().All()) _map.Reset(scope, command);
        _status.Text = "모든 키를 Finale 기본값으로 돌렸습니다.";
        Refresh();
    }
}
