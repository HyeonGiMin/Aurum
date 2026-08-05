using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// 다이어그램 전체를 한 눈에 보여주는 축소 지도. 대형 스키마는 100% 로 보면 지금 어디를
/// 보고 있는지 알 수 없어서 필요하다 (HTML ERD 뷰어의 미니맵 대응).
/// 클릭·드래그하면 그 지점이 화면 가운데로 오도록 <see cref="Navigate"/> 를 올린다.
/// 글자는 그리지 않는다 — 이 크기에서는 읽을 수 없고 비용만 든다.
/// </summary>
public sealed class ErdMiniMap : Control
{
    private static readonly IBrush PaperBrush = new SolidColorBrush(Color.Parse("#FCFCFA"));
    private static readonly IPen ViewPen = new Pen(new SolidColorBrush(Color.Parse("#C8102E")), 1.5);
    private static readonly IBrush ViewFill = new SolidColorBrush(Color.Parse("#C8102E"), 0.10);
    private static readonly IPen GroupPen = new Pen(new SolidColorBrush(Color.Parse("#000000"), 0.16), 1);

    public static readonly StyledProperty<ErdDiagram?> DiagramProperty =
        AvaloniaProperty.Register<ErdMiniMap, ErdDiagram?>(nameof(Diagram));

    /// <summary>지금 화면에 보이는 범위 (다이어그램 좌표계 — 줌과 무관).</summary>
    public static readonly StyledProperty<Rect?> ViewBoxProperty =
        AvaloniaProperty.Register<ErdMiniMap, Rect?>(nameof(ViewBox));

    static ErdMiniMap() => AffectsRender<ErdMiniMap>(DiagramProperty, ViewBoxProperty);

    public ErdMiniMap() => ClipToBounds = true;

    public ErdDiagram? Diagram
    {
        get => GetValue(DiagramProperty);
        set => SetValue(DiagramProperty, value);
    }

    public Rect? ViewBox
    {
        get => GetValue(ViewBoxProperty);
        set => SetValue(ViewBoxProperty, value);
    }

    /// <summary>사용자가 지도에서 고른 지점 (다이어그램 좌표).</summary>
    public event EventHandler<ErdPoint>? Navigate;

    /// <summary>지도 안에서의 배율 — 가로세로 비를 유지한 채 통째로 담는다.</summary>
    private double MapScale(ErdDiagram diagram) =>
        diagram.Width <= 0 || diagram.Height <= 0
            ? 0
            : Math.Min(Bounds.Width / diagram.Width, Bounds.Height / diagram.Height);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(PaperBrush, new Rect(Bounds.Size));

        var diagram = Diagram;
        if (diagram is null) return;
        var scale = MapScale(diagram);
        if (scale <= 0) return;

        foreach (var group in diagram.Groups)
            context.DrawRectangle(null, GroupPen,
                new Rect(group.X * scale, group.Y * scale, group.Width * scale, group.Height * scale));

        foreach (var box in diagram.Boxes)
        {
            var fill = new SolidColorBrush(ErdCanvas.GroupColor(box.GroupIndex));
            // 아주 작아도 점으로는 보이도록 최소 1px 을 준다
            context.FillRectangle(fill, new Rect(
                box.X * scale, box.Y * scale,
                Math.Max(1, box.Width * scale), Math.Max(1, box.Height * scale)));
        }

        if (ViewBox is { } view)
        {
            // 축소해서 보고 있으면 보이는 범위가 다이어그램보다 커진다 —
            // 그대로 그리면 지도 밖으로 삐져나가므로 지도 영역으로 자른다.
            var rect = new Rect(view.X * scale, view.Y * scale, view.Width * scale, view.Height * scale)
                .Intersect(new Rect(0, 0, diagram.Width * scale, diagram.Height * scale));
            if (rect.Width > 0 && rect.Height > 0)
                context.DrawRectangle(ViewFill, ViewPen, rect);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        RaiseNavigate(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Equals(e.Pointer.Captured, this)) RaiseNavigate(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void RaiseNavigate(Point point)
    {
        var diagram = Diagram;
        if (diagram is null) return;
        var scale = MapScale(diagram);
        if (scale <= 0) return;
        Navigate?.Invoke(this, new ErdPoint(point.X / scale, point.Y / scale));
    }
}
