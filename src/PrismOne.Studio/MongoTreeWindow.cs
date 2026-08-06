using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using PrismOne.Db.Core.Mongo;

namespace PrismOne.Studio;

/// <summary>
/// Results &gt; View Documents as Tree — Studio3T 의 Tree View 대응.
/// 그리드(Table View)는 점 경로로 펴서 보여주지만, 여기서는 문서의 **중첩 구조
/// 그대로** 접었다 펴며 본다. 조회 시점의 스냅샷이며 읽기 전용이다.
///
/// 큰 문서에서 창이 굳지 않게 자식 노드는 **펼칠 때 만든다** (lazy).
/// </summary>
public sealed class MongoTreeWindow : Window
{
    public MongoTreeWindow(string title, IReadOnlyList<MongoTreeNode> documents)
    {
        var summary = title.ReplaceLineEndings(" ").Trim();
        if (summary.Length > 60) summary = summary[..60] + "…";
        Title = $"Tree — {summary} ({documents.Count:N0} document(s))";
        Icon = AppIcon.Shared;
        Width = 720;
        Height = 560;
        MinWidth = 380;
        MinHeight = 240;
        ShowInTaskbar = false;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        WindowPlacementTracker.Attach(this, "mongotree");

        var tree = new TreeView { FontSize = 12.5 };
        foreach (var document in documents)
            tree.Items.Add(Build(document));
        Content = tree;
    }

    private TreeViewItem Build(MongoTreeNode node)
    {
        var name = new TextBlock
        {
            Text = node.Name,
            FontWeight = FontWeight.SemiBold,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
        };
        var value = new TextBlock
        {
            Text = node.Value,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 480,
        };
        var type = new TextBlock
        {
            Text = node.Type,
            FontSize = 10.5,
            Opacity = 0.55,
            Margin = new Avalonia.Thickness(10, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var header = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { name, value, type },
        };
        var item = new TreeViewItem { Header = header };
        if (node.Value.Length > 60)
            ToolTip.SetTip(item, node.Value);

        var copy = new MenuItem { Header = "Copy Value" };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(node.Value);
        };
        item.ContextMenu = new ContextMenu { Items = { copy } };

        if (node.HasChildren)
        {
            // lazy: 펼칠 때 실제 자식을 만든다 — 자리표시자 하나로 ▸ 를 띄워 둔다
            var placeholder = new TreeViewItem { Header = "…" };
            item.Items.Add(placeholder);
            item.Expanded += (_, _) =>
            {
                if (!ReferenceEquals(item.Items.Count > 0 ? item.Items[0] : null, placeholder))
                    return;   // 이미 채웠다
                item.Items.Clear();
                foreach (var child in node.Children)
                    item.Items.Add(Build(child));
            };
        }
        return item;
    }
}
