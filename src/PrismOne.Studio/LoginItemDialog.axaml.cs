using Avalonia.Controls;
using Avalonia.Interactivity;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// Golden 의 "Editing existing Login Item" — Login List 항목의 Name/Category/Comment 를 고친다.
/// Category 는 Filter ▾ 의 필터 기준으로 쓰인다.
/// </summary>
public partial class LoginItemDialog : Window
{
    private readonly SavedConnection _target;

    public bool Saved { get; private set; }

    public LoginItemDialog() : this(new SavedConnection("localhost", 5432, "db", "user", null)) { }

    public LoginItemDialog(SavedConnection target)
    {
        _target = target;
        InitializeComponent();
        TargetText.Text = target.DisplayName;
        NameBox.Text = target.Name;
        CategoryBox.Text = target.Category;
        CommentBox.Text = target.Comment;
        NameBox.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        ConnectionStore.UpdateMeta(_target, NameBox.Text, CategoryBox.Text, CommentBox.Text);
        Saved = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
