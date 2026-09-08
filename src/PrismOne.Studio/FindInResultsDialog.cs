using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrismOne.Studio;

/// <summary>
/// 결과 그리드에서 찾기 (Golden 의 "Find in Results…").
///
/// 창을 <b>띄워 둔 채</b> Find Next 를 거듭 누르는 방식이다 — 찾을 때마다 창을 닫았다
/// 열면 이어 찾기가 안 된다. 그래서 모달이 아니라 계속 떠 있는 창으로 만든다.
///
/// 실제 찾기는 <see cref="QueryTabView.FindInResults"/> 가 하고 여기서는 입력만 받는다.
/// 탭을 옮기면 그 탭에서 찾도록 대상 탭을 그때그때 물어본다.
/// </summary>
public sealed class FindInResultsDialog : Window
{
    private readonly Func<QueryTabView?> _target;
    private readonly TextBox _term;
    private readonly CheckBox _matchCase;
    private readonly CheckBox _wholeCell;
    private readonly TextBlock _status;
    private bool _fromStart = true;

    public FindInResultsDialog(Func<QueryTabView?> target)
    {
        _target = target;

        _term = new TextBox { Width = 240, MinHeight = 30, PlaceholderText = "찾을 값" };
        _matchCase = new CheckBox { Content = "대소문자 구분", FontSize = 12.5 };
        _wholeCell = new CheckBox { Content = "셀 전체 일치", FontSize = 12.5 };
        _status = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemeBrushes.Get("TextMutedBrush", "#666666"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
        };

        var next = new Button { Content = "Find Next", MinWidth = 96, MinHeight = 30, IsDefault = true };
        var prev = new Button { Content = "Find Previous", MinWidth = 110, MinHeight = 30 };
        var close = new Button { Content = "Close", MinWidth = 80, MinHeight = 30, IsCancel = true };

        next.Click += (_, _) => Find(backwards: false);
        prev.Click += (_, _) => Find(backwards: true);
        close.Click += (_, _) => Close();
        // 조건이 바뀌면 처음부터 다시 — 안 그러면 예전 자리에서 이어져 헷갈린다
        _term.TextChanged += (_, _) => { _fromStart = true; _status.Text = ""; };
        _matchCase.IsCheckedChanged += (_, _) => _fromStart = true;
        _wholeCell.IsCheckedChanged += (_, _) => _fromStart = true;

        Title = "Find in Results";
        Icon = AppIcon.Shared;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 10,
            Children =
            {
                _term,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 14,
                    Children = { _matchCase, _wholeCell },
                },
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { close, prev, next },
                },
            },
        };

        Opened += (_, _) => _term.Focus();
        // F3 은 관행대로 다음 찾기 (Shift+F3 은 이전)
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.F3) return;
            e.Handled = true;
            Find(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        };
    }

    private void Find(bool backwards)
    {
        if (_term.Text is not { Length: > 0 } term)
        {
            _status.Text = "찾을 값을 넣으세요.";
            return;
        }
        if (_target() is not { } view)
        {
            _status.Text = "결과 탭이 없습니다.";
            return;
        }

        var matchCase = _matchCase.IsChecked == true;
        var wholeCell = _wholeCell.IsChecked == true;
        var found = view.FindInResults(term, matchCase, wholeCell, backwards, _fromStart);
        _fromStart = false;

        if (found)
        {
            var total = view.CountInResults(term, matchCase, wholeCell);
            _status.Text = $"받은 {view.LoadedRowCount:N0}행에서 {total:N0}칸 일치";
            return;
        }

        // 점진 fetch 중이면 "없다"가 아니라 "아직 안 받았다" 일 수 있다 — 구분해서 알린다
        _status.Text = view.LoadedRowCount == 0
            ? "결과가 없습니다."
            : $"받은 {view.LoadedRowCount:N0}행 안에는 없습니다 "
              + "(Ctrl+End 로 끝까지 가져온 뒤 다시 찾아 보세요).";
    }
}
