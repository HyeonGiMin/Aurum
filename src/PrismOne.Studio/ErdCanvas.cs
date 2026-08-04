using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// ErdDiagram 을 직접 그리는 캔버스. 좌표 계산은 Core(ErdLayout)가 하고 여기서는
/// 그리기만 한다 — 같은 좌표를 나중에 SVG 내보내기에도 재사용하기 위해서다.
/// 줌은 RenderTransform 대신 좌표에 배율을 곱해 글자가 뭉개지지 않게 한다.
/// 표기는 SQL Developer Data Modeler 를 따른다 — 주제영역(색 있는 테두리 + 이름),
/// 테이블 박스 전체 채색, 까마귀발 관계선.
/// </summary>
public sealed class ErdCanvas : Control
{
    /// <summary>주제영역 색. 한 화면에서 서로 구분되도록 색상환을 넓게 벌린 파스텔.</summary>
    private static readonly Color[] Palette =
    [
        Color.Parse("#8FD98F"), // green
        Color.Parse("#E79CD2"), // pink
        Color.Parse("#E6D97A"), // yellow
        Color.Parse("#8FC4E6"), // blue
        Color.Parse("#B49CE0"), // purple
        Color.Parse("#DDA45F"), // orange
        Color.Parse("#84D9D2"), // teal
        Color.Parse("#E69090"), // red
        Color.Parse("#BFD97A"), // lime
        Color.Parse("#9FA6E0"), // indigo
    ];

    private static readonly IBrush[] HeaderBrushes =
        Palette.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();
    private static readonly IBrush[] BodyBrushes =
        Palette.Select(c => (IBrush)new SolidColorBrush(Blend(c, Colors.White, 0.55))).ToArray();
    private static readonly IBrush[] GroupFillBrushes =
        Palette.Select(c => (IBrush)new SolidColorBrush(Blend(c, Colors.White, 0.88))).ToArray();
    private static readonly IPen[] GroupPens =
        Palette.Select(c => (IPen)new Pen(new SolidColorBrush(Blend(c, Colors.Black, 0.25)), 1.2)).ToArray();
    private static readonly IBrush[] GroupTitleBrushes =
        Palette.Select(c => (IBrush)new SolidColorBrush(Blend(c, Colors.Black, 0.55))).ToArray();

    private static readonly IBrush PaperBrush = new SolidColorBrush(Color.Parse("#FCFCFA"));
    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#22262B"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#2E3338"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#6C7378"));
    private static readonly IBrush PkBrush = new SolidColorBrush(Color.Parse("#8A5A00"));
    private static readonly IBrush FkBrush = new SolidColorBrush(Color.Parse("#1F4E79"));

    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.Parse("#6E767C")), 1);
    private static readonly IPen ViewBorderPen =
        new Pen(new SolidColorBrush(Color.Parse("#6E767C")), 1) { DashStyle = DashStyle.Dash };
    private static readonly IPen LinePen = new Pen(new SolidColorBrush(Color.Parse("#B04A52")), 1.1);
    private static readonly IPen AccentPen = new Pen(new SolidColorBrush(Color.Parse("#C8102E")), 2.2);
    /// <summary>선택 테이블과 FK 로 이어진 이웃 — 눈에 띄되 선택 자체보다는 약하게.</summary>
    private static readonly IPen RelatedPen = new Pen(new SolidColorBrush(Color.Parse("#D08A96")), 1.8);

    public static readonly StyledProperty<ErdDiagram?> DiagramProperty =
        AvaloniaProperty.Register<ErdCanvas, ErdDiagram?>(nameof(Diagram));

    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ErdCanvas, double>(nameof(Scale), 1.0);

    /// <summary>선택된 테이블 키 — 박스 테두리와 연결된 관계선을 강조한다.</summary>
    public static readonly StyledProperty<string?> SelectedKeyProperty =
        AvaloniaProperty.Register<ErdCanvas, string?>(nameof(SelectedKey));

    /// <summary>선택 테이블과 FK 로 직접 이어진 테이블 키들 — 옅게 강조한다.</summary>
    public static readonly StyledProperty<IReadOnlySet<string>?> RelatedKeysProperty =
        AvaloniaProperty.Register<ErdCanvas, IReadOnlySet<string>?>(nameof(RelatedKeys));

    static ErdCanvas()
    {
        AffectsMeasure<ErdCanvas>(DiagramProperty, ScaleProperty);
        AffectsRender<ErdCanvas>(DiagramProperty, ScaleProperty, SelectedKeyProperty, RelatedKeysProperty);
    }

    public ErdDiagram? Diagram
    {
        get => GetValue(DiagramProperty);
        set => SetValue(DiagramProperty, value);
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public string? SelectedKey
    {
        get => GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    public IReadOnlySet<string>? RelatedKeys
    {
        get => GetValue(RelatedKeysProperty);
        set => SetValue(RelatedKeysProperty, value);
    }

    /// <summary>범례가 캔버스와 같은 색을 쓰도록 팔레트를 공개한다.</summary>
    public static Color GroupColor(int index) => Palette[Math.Abs(index) % Palette.Length];

    /// <summary>화면 좌표(이 컨트롤 기준) 아래에 있는 박스. 없으면 null.</summary>
    public ErdBox? BoxAt(Point point)
    {
        var diagram = Diagram;
        if (diagram is null || Scale <= 0) return null;
        var x = point.X / Scale;
        var y = point.Y / Scale;
        return diagram.Boxes.FirstOrDefault(b => x >= b.X && x <= b.Right && y >= b.Y && y <= b.Bottom);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var diagram = Diagram;
        return diagram is null
            ? default
            : new Size(diagram.Width * Scale, diagram.Height * Scale);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(PaperBrush, new Rect(Bounds.Size));

        var diagram = Diagram;
        if (diagram is null) return;
        var scale = Scale;
        var selected = SelectedKey;
        var related = RelatedKeys;

        foreach (var group in diagram.Groups)
            DrawGroup(context, group, scale);

        foreach (var edge in diagram.Edges)
        {
            var active = selected is not null &&
                         (edge.Relation.ChildKey == selected || edge.Relation.ParentKey == selected);
            DrawEdge(context, edge, scale, active);
        }

        foreach (var box in diagram.Boxes)
        {
            var key = box.Table.Key;
            var outline = key == selected ? AccentPen
                : related?.Contains(key) == true ? RelatedPen
                : box.Table.IsView ? ViewBorderPen
                : BorderPen;
            DrawBox(context, box, scale, outline);
        }
    }

    // ---------- 주제영역 ----------

    private static void DrawGroup(DrawingContext context, ErdGroup group, double scale)
    {
        var index = Math.Abs(group.ColorIndex) % Palette.Length;
        var rect = new Rect(group.X * scale, group.Y * scale, group.Width * scale, group.Height * scale);
        context.DrawRectangle(GroupFillBrushes[index], GroupPens[index], rect, 3 * scale, 3 * scale);

        var padding = ErdMetrics.GroupPadding * scale;
        var title = Text($"{group.Name}  ({group.TableCount})", ErdMetrics.TitleFontSize * scale,
            GroupTitleBrushes[index], bold: true, maxWidth: rect.Width - padding);
        context.DrawText(title, new Point(rect.X + padding * 0.5, rect.Y + 4 * scale));
    }

    // ---------- 관계선 ----------

    private static void DrawEdge(DrawingContext context, ErdEdge edge, double scale, bool active)
    {
        var pen = active ? AccentPen : LinePen;
        var points = edge.Points;
        for (var i = 0; i < points.Count - 1; i++)
            context.DrawLine(pen, Scaled(points[i], scale), Scaled(points[i + 1], scale));

        DrawChildEnd(context, pen, points[0], points[1], scale, edge.Relation);
        DrawParentEnd(context, pen, points[^1], points[^2], scale);
    }

    /// <summary>자식(FK) 쪽 — 1:N 은 까마귀발, 1:1 은 눈금 하나. nullable 이면 앞에 원(0..N).</summary>
    private static void DrawChildEnd(
        DrawingContext context, IPen pen, ErdPoint at, ErdPoint toward, double scale, ErdRelation relation)
    {
        if (!Direction(at, toward, scale, out var origin, out var ux, out var uy)) return;
        var (px, py) = (-uy, ux);
        var prong = 11 * scale;
        var half = 5.5 * scale;
        var fork = new Point(origin.X + ux * prong, origin.Y + uy * prong);

        if (relation.ChildUnique)
            context.DrawLine(pen,
                new Point(fork.X + px * half, fork.Y + py * half),
                new Point(fork.X - px * half, fork.Y - py * half));
        else
        {
            context.DrawLine(pen, fork, origin);
            context.DrawLine(pen, fork, new Point(origin.X + px * half, origin.Y + py * half));
            context.DrawLine(pen, fork, new Point(origin.X - px * half, origin.Y - py * half));
        }

        if (relation.ChildOptional)
        {
            var offset = prong + 6 * scale;
            var center = new Point(origin.X + ux * offset, origin.Y + uy * offset);
            context.DrawEllipse(Brushes.White, pen, center, 3.2 * scale, 3.2 * scale);
        }
    }

    /// <summary>부모(PK) 쪽은 언제나 "하나" — 수직 눈금.</summary>
    private static void DrawParentEnd(
        DrawingContext context, IPen pen, ErdPoint at, ErdPoint toward, double scale)
    {
        if (!Direction(at, toward, scale, out var origin, out var ux, out var uy)) return;
        var (px, py) = (-uy, ux);
        var half = 5.5 * scale;
        var tick = new Point(origin.X + ux * 8 * scale, origin.Y + uy * 8 * scale);
        context.DrawLine(pen,
            new Point(tick.X + px * half, tick.Y + py * half),
            new Point(tick.X - px * half, tick.Y - py * half));
    }

    /// <summary>박스 변 위의 점에서 선이 뻗어나가는 방향(단위 벡터).</summary>
    private static bool Direction(
        ErdPoint at, ErdPoint toward, double scale, out Point origin, out double ux, out double uy)
    {
        origin = Scaled(at, scale);
        var target = Scaled(toward, scale);
        var dx = target.X - origin.X;
        var dy = target.Y - origin.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.01)
        {
            (ux, uy) = (0, 0);
            return false;
        }
        (ux, uy) = (dx / length, dy / length);
        return true;
    }

    // ---------- 테이블 박스 ----------

    private static void DrawBox(DrawingContext context, ErdBox box, double scale, IPen outline)
    {
        var index = Math.Abs(box.GroupIndex) % Palette.Length;
        var rect = new Rect(box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale);
        // Data Modeler 처럼 박스 전체를 주제영역 색으로 채우고 헤더만 진하게.
        context.DrawRectangle(BodyBrushes[index], outline, rect);

        var headerHeight = ErdMetrics.HeaderHeight * scale;
        var header = new Rect(rect.X, rect.Y, rect.Width, headerHeight);
        context.DrawRectangle(HeaderBrushes[index], null, header);
        context.DrawLine(BorderPen,
            new Point(rect.X, rect.Y + headerHeight),
            new Point(rect.Right, rect.Y + headerHeight));

        var padding = ErdMetrics.Padding * scale;
        var title = Text(box.Table.Name, ErdMetrics.TitleFontSize * scale, TitleBrush,
            bold: true, maxWidth: rect.Width - padding * 2);
        context.DrawText(title, new Point(rect.X + padding, rect.Y + (headerHeight - title.Height) / 2));

        var rowHeight = ErdMetrics.RowHeight * scale;
        var fontSize = ErdMetrics.FontSize * scale;
        var markerWidth = 22 * scale;
        var y = rect.Y + headerHeight + 3 * scale;

        foreach (var column in box.VisibleColumns)
        {
            if (column.IsPk || column.IsFk)
            {
                var marker = Text(column.IsPk ? "PK" : "FK", fontSize * 0.85,
                    column.IsPk ? PkBrush : FkBrush, bold: true);
                context.DrawText(marker, new Point(rect.X + padding, y + 2 * scale));
            }

            // Data Modeler 관례: NOT NULL 은 * 로 표시
            var label = Text($"{column.Name}{(column.NotNull ? " *" : "")}  {column.Type}", fontSize,
                column.NotNull ? TextBrush : MutedBrush,
                maxWidth: rect.Width - padding * 2 - markerWidth);
            context.DrawText(label, new Point(rect.X + padding + markerWidth, y));
            y += rowHeight;
        }

        if (box.HiddenColumnCount > 0)
        {
            var more = Text($"… +{box.HiddenColumnCount} more", fontSize * 0.9, MutedBrush,
                maxWidth: rect.Width - padding * 2);
            context.DrawText(more, new Point(rect.X + padding + markerWidth, y));
        }
    }

    private static FormattedText Text(
        string value, double size, IBrush brush, bool bold = false, double maxWidth = double.PositiveInfinity)
    {
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal,
            bold ? FontWeight.SemiBold : FontWeight.Normal);
        var text = new FormattedText(
            value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, Math.Max(1, size), brush);
        if (!double.IsPositiveInfinity(maxWidth))
        {
            text.MaxTextWidth = Math.Max(1, maxWidth);
            text.Trimming = TextTrimming.CharacterEllipsis;
        }
        return text;
    }

    private static Point Scaled(ErdPoint point, double scale) => new(point.X * scale, point.Y * scale);

    /// <summary>color 를 target 쪽으로 amount(0~1)만큼 섞는다.</summary>
    private static Color Blend(Color color, Color target, double amount) => Color.FromRgb(
        (byte)(color.R + (target.R - color.R) * amount),
        (byte)(color.G + (target.G - color.G) * amount),
        (byte)(color.B + (target.B - color.B) * amount));
}
