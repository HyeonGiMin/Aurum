using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using PrismOne.Db.Core;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Studio;

/// <summary>
/// Golden 의 SQLBuilder — 테이블·컬럼·조인·조건을 골라 SELECT 를 만들고 에디터에 넣는다.
/// 여기서 실행하지는 않는다 (사용자가 에디터에서 확인하고 F9).
///
/// 테이블을 둘 이상 넣으면 JOIN 이 되고, ON 조건은 카탈로그의 FK 에서 찾아 채운다
/// (없으면 직접 적는다). 집계를 하나라도 넣으면 고른 컬럼이 GROUP BY 로 나간다.
/// </summary>
public partial class SqlBuilderDialog : Window
{
    /// <summary>쿼리에 들어간 테이블 한 줄 — 첫 줄은 FROM, 나머지는 JOIN.</summary>
    private sealed class Entry
    {
        public required TableInfo Table { get; init; }
        public required string Alias { get; init; }
        public List<string> Columns { get; set; } = [];
        public JoinKind Kind { get; set; } = JoinKind.Inner;
        public List<JoinOn> On { get; set; } = [];
    }

    private readonly List<TableInfo> _tables;
    private readonly ConnectionProfile? _profile;
    private readonly Dictionary<string, List<ColumnInfo>> _columnCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> _entries = [];

    /// <summary>FK 관계 (ON 자동 채움용). 두 번째 테이블을 넣을 때 한 번만 읽는다.</summary>
    private IReadOnlyList<ErdRelation>? _relations;
    private bool _relationsTried;

    /// <summary>테이블 추가가 도는 중. 겹쳐 들어오면 별칭이 겹치고 FROM/JOIN 순서가 어긋난다.</summary>
    private bool _adding;

    /// <summary>패널을 다시 그리는 동안 미리보기를 멈춘다 (ItemsSource 교체가 이벤트를 쏟아낸다).</summary>
    private bool _rebuilding;

    /// <summary>확인을 눌렀을 때 만들어진 SQL. 취소면 null.</summary>
    public string? Result { get; private set; }

    public SqlBuilderDialog() : this([], null) { }

    public SqlBuilderDialog(IEnumerable<TableInfo> tables, ConnectionProfile? profile)
    {
        _tables = tables.ToList();
        _profile = profile;
        InitializeComponent();
        RefreshTableList();
        BuildJoinRows();
    }

    /// <summary>접속당 공용 introspection 캐시 (MainWindow 가 주입). 없으면 직접 조회한다.</summary>
    public SchemaCache? SchemaCache { get; set; }

    /// <summary>
    /// 스크린샷 하니스 전용 — 접속 없이 테이블을 넣어 화면을 채운다.
    /// FK 를 못 읽는 상황이므로 ON 은 인자로 받아 채운다.
    /// </summary>
    internal async Task AddForShotAsync(string qualifiedName, string? on = null)
    {
        TableList.SelectedItem = (TableList.ItemsSource as IEnumerable<string>)
            ?.FirstOrDefault(x => x == qualifiedName);
        await AddSelectedTableAsync();
        if (on is not null && _entries.Count > 1)
        {
            _entries[^1].On = ParseOn(on);
            RebuildAll();
        }
    }

    // ---------- 왼쪽 테이블 목록 ----------

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

    private async void OnTableActivated(object? sender, TappedEventArgs e) => await AddSelectedTableAsync();

    private async void OnAddTable(object? sender, RoutedEventArgs e) => await AddSelectedTableAsync();

    private async Task AddSelectedTableAsync()
    {
        // 컬럼·FK 조회를 기다리는 동안 또 들어오면 같은 별칭이 두 번 나오고,
        // 둘 다 "첫 테이블"로 보여 뜻하지 않은 cross join 이 된다
        if (_adding)
            return;
        if (TableList.SelectedItem is not string qualified)
        {
            StatusText.Text = "왼쪽에서 테이블을 고르세요.";
            return;
        }
        if (_tables.FirstOrDefault(t => t.QualifiedName == qualified) is not { } table)
            return;

        _adding = true;
        try
        {
            // 별칭과 자리를 await 전에 확정한다 — 순서가 사용자가 누른 순서와 같아야 한다
            var isJoin = _entries.Count > 0;
            var entry = new Entry { Table = table, Alias = NextAlias(table) };
            _entries.Add(entry);
            BuildJoinRows();

            entry.Columns = await LoadColumnsAsync(table);

            // 두 번째부터는 JOIN — FK 를 찾아 ON 을 채운다
            if (isJoin)
            {
                await EnsureRelationsAsync();
                entry.On = SuggestOn(entry);
                if (entry.On.Count == 0)
                    StatusText.Text = $"{table.Name}: FK 를 찾지 못했습니다 — ON 을 직접 적으세요";
            }

            RebuildAll();
        }
        finally
        {
            _adding = false;
        }
    }

    /// <summary>테이블 이름 첫 글자로 별칭을 만들고, 겹치면 숫자를 붙인다 (s, p, s2 …).</summary>
    private string NextAlias(TableInfo table)
    {
        var seed = new string(table.Name.Where(char.IsLetter).Take(1).ToArray()).ToLowerInvariant();
        if (seed.Length == 0)
            seed = "t";
        var alias = seed;
        for (var n = 2; _entries.Any(x => x.Alias == alias); n++)
            alias = seed + n;
        return alias;
    }

    // ---------- FK 로 ON 채우기 ----------

    /// <summary>
    /// FK 는 ERD 카탈로그에서 읽는다. Oracle 은 관계까지 읽으면 매우 느릴 수 있어
    /// (STATUS 기록상 수십 초) 실패해도 빌더는 그대로 쓸 수 있게 둔다.
    /// </summary>
    private async Task EnsureRelationsAsync()
    {
        if (_relationsTried || _profile is null)
            return;
        var schemas = _entries.Select(x => x.Table.Schema)
            .Concat(_tables.Select(t => t.Schema))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        try
        {
            StatusText.Text = "FK 관계를 읽는 중…";
            var graph = await _profile.Provider.CreateErdCatalog(_profile).LoadAsync(schemas);
            _relations = graph.Relations;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            _relations = null;
            StatusText.Text = $"FK 를 읽지 못했습니다 — ON 을 직접 적으세요 ({ex.Message})";
        }
        finally
        {
            // 조회가 끝난 뒤에 표시한다 — 먼저 세우면 아직 읽는 중인데 "못 찾았다"고 하게 된다.
            // 실패해도 다시 시도하지 않는다 (Oracle 은 관계 조회가 매우 비싸다).
            _relationsTried = true;
        }
    }

    /// <summary>이미 들어간 테이블 중 하나와 FK 로 이어지면 그 짝을 ON 으로 돌려준다.</summary>
    private List<JoinOn> SuggestOn(Entry incoming)
    {
        if (_relations is not { Count: > 0 })
            return [];

        // 자기 자신과는 맞추지 않는다 — self FK 가 있는 테이블이 자기 별칭으로 조인돼 버린다
        foreach (var existing in _entries.Where(x => !ReferenceEquals(x, incoming)))
        {
            foreach (var rel in _relations)
            {
                // 새 테이블이 자식이고 기존 테이블이 부모
                if (Same(rel.ChildKey, incoming.Table) && Same(rel.ParentKey, existing.Table))
                    return Pair(rel.ChildColumns, incoming.Alias, rel.ParentColumns, existing.Alias);
                // 반대 방향
                if (Same(rel.ParentKey, incoming.Table) && Same(rel.ChildKey, existing.Table))
                    return Pair(rel.ParentColumns, incoming.Alias, rel.ChildColumns, existing.Alias);
            }
        }
        return [];

        static bool Same(string key, TableInfo table) =>
            string.Equals(key, $"{table.Schema}.{table.Name}", StringComparison.OrdinalIgnoreCase);

        static List<JoinOn> Pair(
            IReadOnlyList<string> left, string leftAlias, IReadOnlyList<string> right, string rightAlias) =>
            left.Zip(right, (l, r) => new JoinOn($"{leftAlias}.{l}", $"{rightAlias}.{r}")).ToList();
    }

    // ---------- 오른쪽 편집 영역 ----------

    private void RebuildAll()
    {
        _rebuilding = true;
        try
        {
            BuildJoinRows();
            BuildColumnChecks();
            RebuildOrderCombo();
            PruneDependentRows();
        }
        finally
        {
            _rebuilding = false;
        }
        UpdatePreview();
    }

    /// <summary>
    /// 빠진 테이블을 가리키는 조건·집계 줄을 걷어내고, 남은 줄의 컬럼 목록을 새로 고친다.
    /// 그러지 않으면 FROM 에 없는 별칭이 WHERE 에 남아 <b>실행할 때에야</b> 터진다.
    /// </summary>
    private void PruneDependentRows()
    {
        var valid = QualifiedColumns();

        foreach (var row in ConditionPanel.Children.OfType<StackPanel>().ToList())
        {
            if (row.Children.OfType<ComboBox>().FirstOrDefault() is not { } combo)
                continue;
            var selected = combo.SelectedItem as string;
            if (selected is { Length: > 0 } && !valid.Contains(selected))
            {
                ConditionPanel.Children.Remove(row);
                continue;
            }
            combo.ItemsSource = valid;
            combo.SelectedItem = selected;
        }

        foreach (var row in AggregatePanel.Children.OfType<StackPanel>().ToList())
        {
            var combos = row.Children.OfType<ComboBox>().ToList();
            if (combos.Count < 2)
                continue;
            var selected = combos[1].SelectedItem as string;
            if (selected is { Length: > 0 } && !valid.Contains(selected))
            {
                AggregatePanel.Children.Remove(row);
                continue;
            }
            combos[1].ItemsSource = new[] { "" }.Concat(valid).ToList();
            combos[1].SelectedItem = selected ?? "";
        }
    }

    private void BuildJoinRows()
    {
        JoinPanel.Children.Clear();
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var first = i == 0;
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
                Margin = new Avalonia.Thickness(0, 1),
            };

            var kindCombo = new ComboBox
            {
                ItemsSource = new[] { "from", "join", "left join", "right join", "full join" },
                SelectedIndex = first ? 0 : (int)entry.Kind + 1,
                IsEnabled = !first,
                FontSize = 12,
                MinWidth = 96,
                VerticalAlignment = VerticalAlignment.Center,
            };
            kindCombo.SelectionChanged += (_, _) =>
            {
                if (!first && kindCombo.SelectedIndex >= 1)
                {
                    entry.Kind = (JoinKind)(kindCombo.SelectedIndex - 1);
                    UpdatePreview();
                }
            };

            var label = new TextBlock
            {
                Text = $" {entry.Table.QualifiedName} {entry.Alias} ",
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(6, 0),
            };

            var on = new TextBox
            {
                Text = string.Join(" and ", entry.On.Select(o => $"{o.Left} = {o.Right}")),
                PlaceholderText = first ? "" : "예: s.patient_key = p.patient_key",
                IsEnabled = !first,
                FontSize = 12,
                MinHeight = 26,
                VerticalAlignment = VerticalAlignment.Center,
            };
            on.TextChanged += (_, _) => { entry.On = ParseOn(on.Text); UpdatePreview(); };

            var remove = new Button
            {
                Content = "✕",
                FontSize = 12,
                Padding = new Avalonia.Thickness(7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(6, 0, 0, 0),
            };
            remove.Click += (_, _) =>
            {
                _entries.Remove(entry);
                RebuildAll();
            };

            Grid.SetColumn(kindCombo, 0);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(on, 2);
            Grid.SetColumn(remove, 3);
            row.Children.Add(kindCombo);
            row.Children.Add(label);
            row.Children.Add(on);
            row.Children.Add(remove);
            JoinPanel.Children.Add(row);
        }

        if (_entries.Count == 0)
            JoinPanel.Children.Add(new TextBlock
            {
                Text = "왼쪽에서 테이블을 골라 \"쿼리에 추가\" 하세요.",
                FontSize = 12,
                Opacity = 0.6,
            });
    }

    /// <summary>"a.x = b.y and a.z = b.w" 를 짝으로 되돌린다. 형식이 어긋난 줄은 버린다.</summary>
    private static List<JoinOn> ParseOn(string? text) =>
        (text ?? "")
        .Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
        .Where(sides => sides.Length == 2 && sides[0].Length > 0 && sides[1].Length > 0)
        .Select(sides => new JoinOn(sides[0], sides[1]))
        .ToList();

    private void BuildColumnChecks()
    {
        // 테이블을 하나 더 붙였다고 앞서 고른 컬럼이 사라지면 안 된다 — 체크 상태를 옮겨 담는다
        var wasChecked = ColumnPanel.Children
            .OfType<CheckBox>()
            .Where(b => b.IsChecked == true)
            .Select(b => (string)b.Tag!)
            .ToHashSet(StringComparer.Ordinal);

        ColumnPanel.Children.Clear();
        foreach (var entry in _entries)
        {
            ColumnPanel.Children.Add(new TextBlock
            {
                Text = $"{entry.Alias} — {entry.Table.QualifiedName}",
                FontSize = 11.5,
                Opacity = 0.65,
                Margin = new Avalonia.Thickness(0, 6, 0, 2),
            });
            foreach (var column in entry.Columns)
            {
                var tag = $"{entry.Alias}.{column}";
                var box = new CheckBox
                {
                    // 문자열을 그대로 주면 '_' 가 단축키 표시로 먹혀 study_key 가 studykey 로 보인다
                    Content = new TextBlock { Text = column, FontSize = 12.5 },
                    MinHeight = 22,
                    Tag = tag,
                    IsChecked = wasChecked.Contains(tag),
                };
                // 상태를 되돌린 뒤에 붙인다 — 먼저 붙이면 복원 때마다 미리보기가 다시 그려진다
                box.IsCheckedChanged += OnSpecCheckChanged;
                ColumnPanel.Children.Add(box);
            }
        }
    }

    private void RebuildOrderCombo()
    {
        var previous = OrderColumnCombo.SelectedItem as string;
        var all = new List<string> { "" };
        all.AddRange(QualifiedColumns());
        OrderColumnCombo.ItemsSource = all;
        OrderColumnCombo.SelectedIndex = previous is not null && all.Contains(previous)
            ? all.IndexOf(previous)
            : 0;
    }

    private List<string> QualifiedColumns() =>
        _entries.SelectMany(e => e.Columns.Select(c => $"{e.Alias}.{c}")).ToList();

    private async Task<List<string>> LoadColumnsAsync(TableInfo table)
    {
        if (_columnCache.TryGetValue(table.QualifiedName, out var cached))
            return cached.Select(c => c.Name).ToList();
        if (SchemaCache is { } cache)
        {
            var fromCache = (await cache.GetColumnsAsync(table)).ToList();
            _columnCache[table.QualifiedName] = fromCache;
            return fromCache.Select(c => c.Name).ToList();
        }
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
            return columns.Select(c => c.Name).ToList();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"컬럼을 읽지 못했습니다: {ex.Message}";
            return [];
        }
    }

    // ---------- 조건 · 집계 줄 ----------

    private void OnAddCondition(object? sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
        {
            StatusText.Text = "테이블을 먼저 추가하세요.";
            return;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var column = new ComboBox { ItemsSource = QualifiedColumns(), FontSize = 12, MinWidth = 190 };
        var op = new ComboBox
        {
            ItemsSource = SqlBuilder.Operators,
            SelectedIndex = 0,
            FontSize = 12,
            MinWidth = 96,
        };
        var value = new TextBox { Width = 170, MinHeight = 26, FontSize = 12, PlaceholderText = "값" };
        var remove = new Button { Content = "✕", FontSize = 12, Padding = new Avalonia.Thickness(7, 2) };

        column.SelectionChanged += OnSpecSelectionChanged;
        op.SelectionChanged += OnSpecSelectionChanged;
        value.TextChanged += OnSpecTextChanged;
        remove.Click += (_, _) => { ConditionPanel.Children.Remove(row); UpdatePreview(); };

        row.Children.Add(column);
        row.Children.Add(op);
        row.Children.Add(value);
        row.Children.Add(remove);
        ConditionPanel.Children.Add(row);
        UpdatePreview();
    }

    private void OnAddAggregate(object? sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
        {
            StatusText.Text = "테이블을 먼저 추가하세요.";
            return;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var fn = new ComboBox
        {
            ItemsSource = SqlBuilder.AggregateFunctions,
            SelectedIndex = 0,
            FontSize = 12,
            MinWidth = 96,
        };
        var column = new ComboBox
        {
            // count 는 컬럼 없이 count(*) 가 되므로 빈 항목을 맨 앞에 둔다
            ItemsSource = new[] { "" }.Concat(QualifiedColumns()).ToList(),
            SelectedIndex = 0,
            FontSize = 12,
            MinWidth = 190,
        };
        var alias = new TextBox { Width = 120, MinHeight = 26, FontSize = 12, PlaceholderText = "별칭" };
        var remove = new Button { Content = "✕", FontSize = 12, Padding = new Avalonia.Thickness(7, 2) };

        fn.SelectionChanged += OnSpecSelectionChanged;
        column.SelectionChanged += OnSpecSelectionChanged;
        alias.TextChanged += OnSpecTextChanged;
        remove.Click += (_, _) => { AggregatePanel.Children.Remove(row); UpdatePreview(); };

        row.Children.Add(fn);
        row.Children.Add(column);
        row.Children.Add(alias);
        row.Children.Add(remove);
        AggregatePanel.Children.Add(row);
        UpdatePreview();
    }

    // XAML 은 이벤트 시그니처가 정확히 맞아야 연결된다 (EventHandler<T> 별로 하나씩)
    private void OnSpecCheckChanged(object? sender, RoutedEventArgs e) => UpdatePreview();
    private void OnSpecSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void OnSpecTextChanged(object? sender, TextChangedEventArgs e) => UpdatePreview();

    // ---------- 미리보기 ----------

    private QuerySpec? CurrentSpec()
    {
        if (_entries.Count == 0)
            return null;
        var root = _entries[0];

        var columns = ColumnPanel.Children
            .OfType<CheckBox>()
            .Where(b => b.IsChecked == true)
            .Select(b => (string)b.Tag!)
            .ToList();

        var conditions = ConditionPanel.Children.OfType<StackPanel>()
            .Select(row =>
            {
                var combos = row.Children.OfType<ComboBox>().ToList();
                var text = row.Children.OfType<TextBox>().FirstOrDefault();
                return combos.Count >= 2 && combos[0].SelectedItem is string col
                    ? new QueryCondition(col, combos[1].SelectedItem as string ?? "=", text?.Text)
                    : null;
            })
            .OfType<QueryCondition>()
            .ToList();

        var aggregates = AggregatePanel.Children.OfType<StackPanel>()
            .Select(row =>
            {
                var combos = row.Children.OfType<ComboBox>().ToList();
                var alias = row.Children.OfType<TextBox>().FirstOrDefault()?.Text;
                return combos.Count >= 2 && combos[0].SelectedItem is string function
                    ? new QueryAggregate(function, combos[1].SelectedItem as string, alias)
                    : null;
            })
            .OfType<QueryAggregate>()
            .ToList();

        var orders = new List<QueryOrder>();
        if (OrderColumnCombo.SelectedItem is string order && order.Length > 0)
            orders.Add(new QueryOrder(order, OrderDescBox.IsChecked == true));

        var joins = _entries.Skip(1)
            .Select(x => new QueryJoin(x.Table.QualifiedName, x.Alias, x.Kind, x.On))
            .ToList();

        int? limit = int.TryParse(LimitBox.Text, out var parsed) && parsed > 0 ? parsed : null;
        var dialect = _profile?.Kind == DbKind.Oracle ? SqlDialect.Oracle : SqlDialect.Standard;

        return new QuerySpec(
            root.Table.QualifiedName, columns, conditions, orders, limit, root.Alias,
            joins, aggregates, DistinctBox.IsChecked == true, dialect);
    }

    private void UpdatePreview()
    {
        if (_rebuilding)
            return;
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
