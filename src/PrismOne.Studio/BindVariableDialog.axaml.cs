using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>다이얼로그 한 줄 (변수 이름 + 입력값).</summary>
public sealed class BindEntry
{
    public required string Name { get; init; }
    public string Label => $":{Name}";
    public string Value { get; set; } = "";
}

/// <summary>Golden 의 바인드 변수 프롬프트. 값은 탭별로 기억된다.</summary>
public partial class BindVariableDialog : Window
{
    private readonly ObservableCollection<BindEntry> _entries = [];

    /// <summary>OK 를 누른 경우에만 값 사전, 취소면 null.</summary>
    public Dictionary<string, string?>? Result { get; private set; }

    public BindVariableDialog() : this([], new Dictionary<string, string?>()) { }

    public BindVariableDialog(IReadOnlyList<BindVariable> variables, IReadOnlyDictionary<string, string?> previous)
    {
        InitializeComponent();
        foreach (var v in variables)
        {
            _entries.Add(new BindEntry
            {
                Name = v.Name,
                Value = previous.TryGetValue(v.Name, out var prior) ? prior ?? "" : "",
            });
        }
        VariableList.ItemsSource = _entries;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Result = _entries.ToDictionary(
            entry => entry.Name,
            entry => string.IsNullOrEmpty(entry.Value) ? null : entry.Value);
        Close();
    }
}
