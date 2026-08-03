using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// Golden 의 SQLBuilder — 테이블·컬럼·조건을 골라 SELECT 를 만들고 에디터에 넣는다.
/// 여기서 실행하지는 않는다 (사용자가 에디터에서 확인하고 F9).
/// </summary>
public partial class SqlBuilderDialog : Window
{
    private readonly List<TableInfo> _tables;
    private readonly ConnectionProfile? _profile;
    private readonly Dictionary<string, List<ColumnInfo>> _columnCache = new(StringComparer.OrdinalIgnoreCase);

    private TableInfo? _table;
    private List<string> _columns = [];

    /// <summary>확인을 눌렀을 때 만들어진 SQL. 취소면 null.</summary>
    public string? Result { get; private set; }

    public SqlBuilderDialog() : this([], null) { }

    public SqlBuilderDialog(IEnumerable<TableInfo> tables, ConnectionProfile? profile)
    {
        _tables = tables.ToList();
        _profile = profile;
        InitializeComponent();
        RefreshTableList();
    }

    private void RefreshTableList()
    {
        var filter = TableFilterBox.Text?.Trim() ?? "";
        TableList.ItemsSource = _tables
            .Where(t => filter.Length == 0
                     || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                     || t.Schema.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.QualifiedName)
            .ToList();
    }

    private void OnTableFilterChanged(object? sender, TextChangedEventArgs e) => RefreshTableList();

    private async void OnTableSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (TableList.SelectedItem is not string qualified)
            return;
        _table = _tables.FirstOrDefault(t => t.QualifiedName == qualified);
        if (_table is null)
            return;

        _columns = await LoadColumnsAsync(_table);
        BuildColumnChecks();
        OrderColumnCombo.ItemsSource = new[] { "" }.Concat(_columns).ToList();
        OrderColumnCombo.SelectedIndex = 0;
        ConditionPanel.Children.Clear();
        UpdatePreview();
    }

    private async Task<List<string>> LoadColumnsAsync(TableInfo table)
    {
        if (_columnCache.TryGetValue(table.QualifiedName, out var cached))
            return cached.Select(c => c.Name).ToList();
        if (_profile is null)
        {
            StatusText.Text = "미접속 — 컬럼 목록 없이 * 로 만듭니다";
            return [];
        }
        try
        {
            await using var conn = await _profile.OpenAsync();
            var columns = await SchemaCatalog.GetColumnsAsync(conn, table);
            _columnCache[table.QualifiedName] = columns;
            StatusText.Text = "";
            return columns.Select(c => c.Name).ToList();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"컬럼 조회 실패: {ex.Message}";
            return [];
        }
    }

    /// <summary>컬럼 체크박스 — 아무것도 고르지 않으면 `*`.</summary>
    private void BuildColumnChecks()
    {
        ColumnPanel.Children.Clear();
        foreach (var column in _columns)
        {
            var box = new CheckBox { Content = column, FontSize = 12.5, Tag = column };
            box.IsCheckedChanged += OnSpecChanged;
            ColumnPanel.Children.Add(box);
        }
    }

    private void OnAddCondition(object? sender, RoutedEventArgs e)
    {
        var column = new ComboBox { ItemsSource = _columns, Width = 190, FontSize = 12 };
        var op = new ComboBox { ItemsSource = SqlBuilder.Operators, SelectedIndex = 0, Width = 110, FontSize = 12 };
        var value = new TextBox { Width = 200, MinHeight = 28, FontSize = 12, PlaceholderText = "값" };
        var remove = new Button { Content = "✕", FontSize = 12, Padding = new Avalonia.Thickness(8, 2) };

        column.SelectionChanged += OnSpecChanged;
        op.SelectionChanged += OnSpecChanged;
        value.TextChanged += OnSpecChanged;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { column, op, value, remove },
        };
        remove.Click += (_, _) =>
        {
            ConditionPanel.Children.Remove(row);
            UpdatePreview();
        };
        ConditionPanel.Children.Add(row);
        UpdatePreview();
    }

    private void OnSpecChanged(object? sender, EventArgs e) => UpdatePreview();

    // XAML 은 이벤트 시그니처가 정확히 맞아야 연결된다 (EventHandler<T> 별로 하나씩)
    private void OnSpecCheckChanged(object? sender, RoutedEventArgs e) => UpdatePreview();
    private void OnSpecSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void OnSpecTextChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private QuerySpec? CurrentSpec()
    {
        if (_table is null)
            return null;

        var columns = ColumnPanel.Children
            .OfType<CheckBox>()
            .Where(b => b.IsChecked == true)
            .Select(b => (string)b.Tag!)
            .ToList();

        var conditions = new List<QueryCondition>();
        foreach (var row in ConditionPanel.Children.OfType<StackPanel>())
        {
            var combos = row.Children.OfType<ComboBox>().ToList();
            var text = row.Children.OfType<TextBox>().FirstOrDefault();
            if (combos.Count < 2 || combos[0].SelectedItem is not string column)
                continue;
            conditions.Add(new QueryCondition(column, combos[1].SelectedItem as string ?? "=", text?.Text));
        }

        var orders = new List<QueryOrder>();
        if (OrderColumnCombo.SelectedItem is string order && order.Length > 0)
            orders.Add(new QueryOrder(order, OrderDescBox.IsChecked == true));

        int? limit = int.TryParse(LimitBox.Text, out var parsed) && parsed > 0 ? parsed : null;
        var alias = UseAliasBox.IsChecked == true ? "s" : null;
        return new QuerySpec(_table.QualifiedName, columns, conditions, orders, limit, alias);
    }

    private void UpdatePreview()
    {
        if (CurrentSpec() is not { } spec)
        {
            PreviewBox.Text = "";
            return;
        }
        try
        {
            PreviewBox.Text = SqlBuilder.Build(spec);
        }
        catch (ArgumentException ex)
        {
            PreviewBox.Text = "";
            StatusText.Text = ex.Message;
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (PreviewBox.Text is not { Length: > 0 } sql)
        {
            StatusText.Text = "테이블을 고르세요.";
            return;
        }
        Result = sql;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
