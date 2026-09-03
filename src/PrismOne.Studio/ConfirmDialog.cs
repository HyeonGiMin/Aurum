using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrismOne.Studio;

/// <summary>
/// 예/아니오 확인 창 — 그리드 삭제처럼 되돌리기 어려운 동작에만 쓴다
/// (Golden: "Delete %d selected records?").
/// </summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window owner, string message)
    {
        var result = false;
        var yes = new Button { Content = "Yes", MinWidth = 80, MinHeight = 30, IsDefault = true };
        var no = new Button { Content = "No", MinWidth = 80, MinHeight = 30, IsCancel = true };
        var dialog = new Window
        {
            Title = "Aurum",
            Icon = AppIcon.Shared,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            // Golden 처럼 작업표시줄에는 메인 창 하나만 — 부속 창은 창 선택 목록에 안 뜬다
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { no, yes },
                    },
                },
            },
        };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return result;
    }
}
