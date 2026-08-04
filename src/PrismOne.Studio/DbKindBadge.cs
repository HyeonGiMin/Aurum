using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PrismOne.Db.Core.Providers;

namespace PrismOne.Studio;

/// <summary>
/// Login List 의 DB 종류 배지 색. 목록에서 종류를 글자보다 색으로 먼저 알아보게 한다.
/// 색은 각 제품 브랜드 색을 쓴다 — 전부 흰 글자와 대비가 충분하다.
/// </summary>
public sealed class DbKindBadge : IValueConverter
{
    private static readonly IBrush PostgreSql = New("#336791");
    private static readonly IBrush Oracle = New("#C74634");
    private static readonly IBrush Sqlite = New("#003B57");
    private static readonly IBrush MongoDb = New("#00684A");
    private static readonly IBrush Unknown = New("#6E767C");

    /// <summary>XAML 바인딩용 인스턴스 — {x:Static local:DbKindBadge.Background}</summary>
    public static DbKindBadge Background { get; } = new();

    public static IBrush BrushFor(DbKind kind) => kind switch
    {
        DbKind.PostgreSql => PostgreSql,
        DbKind.Oracle => Oracle,
        DbKind.Sqlite => Sqlite,
        DbKind.MongoDb => MongoDb,
        _ => Unknown,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BrushFor(value is DbKind kind ? kind : DbKind.PostgreSql);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("표시 전용 컨버터입니다.");

    private static IBrush New(string hex) => new SolidColorBrush(Color.Parse(hex));
}
