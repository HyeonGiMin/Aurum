using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrismOne.Studio;

/// <summary>
/// 실패 이유를 알리는 팝업. 접속 실패는 상태바 한 줄로는 놓치기 쉬워서 창으로 띄운다.
///
/// **접속 문자열과 비밀번호는 넣지 않는다** — 예외 메시지·타입·내부 예외까지만 보여준다.
/// </summary>
public static class ErrorDialog
{
    public static Task ShowAsync(Window owner, string title, string summary, Exception error) =>
        ShowAsync(owner, title, summary, Describe(error));

    public static async Task ShowAsync(Window owner, string title, string summary, string detail)
    {
        var close = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 84,
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var dialog = new Window
        {
            Title = title,
            Icon = AppIcon.Shared,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = summary,
                        FontSize = 13.5,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        BorderBrush = new SolidColorBrush(Color.Parse("#C8C8C8")),
                        Background = new SolidColorBrush(Color.Parse("#FAFAFA")),
                        Padding = new Avalonia.Thickness(10, 8),
                        CornerRadius = new Avalonia.CornerRadius(3),
                        Child = new ScrollViewer
                        {
                            MaxHeight = 260,
                            Content = new SelectableTextBlock
                            {
                                Text = detail,
                                FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
                                FontSize = 12,
                                TextWrapping = TextWrapping.Wrap,
                            },
                        },
                    },
                    close,
                },
            },
        };

        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    /// <summary>드라이버 예외는 내부 예외에 진짜 이유가 있는 경우가 많다.</summary>
    private static string Describe(Exception error)
    {
        var text = new StringBuilder();
        text.Append(error.GetType().Name).Append(": ").Append(error.Message);
        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
            text.AppendLine().AppendLine().Append("→ ").Append(inner.GetType().Name)
                .Append(": ").Append(inner.Message);
        return text.ToString();
    }
}
