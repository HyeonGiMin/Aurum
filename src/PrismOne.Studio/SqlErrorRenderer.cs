using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>
/// SQL 검증 결과를 빨간 물결 밑줄로 그린다 (DataGrip 의 unresolved reference 표시).
/// 검증 자체는 <see cref="SqlValidator"/>(Core) — 여기는 그리기만 한다.
/// </summary>
public sealed class SqlErrorRenderer : IBackgroundRenderer
{
    private static readonly Pen Squiggle = new(new SolidColorBrush(Color.Parse("#D64541")), 1);

    public IReadOnlyList<SqlIssue> Issues { get; set; } = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Issues.Count == 0 || textView.Document is not { } doc)
            return;
        textView.EnsureVisualLines();

        foreach (var issue in Issues)
        {
            if (issue.Start >= doc.TextLength) continue;
            var segment = new TextSegment
            {
                StartOffset = issue.Start,
                Length = Math.Min(issue.Length, doc.TextLength - issue.Start),
            };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                DrawSquiggle(drawingContext, rect);
        }
    }

    private static void DrawSquiggle(DrawingContext dc, Rect rect)
    {
        var y = rect.Bottom - 1;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(rect.Left, y), false);
            var up = true;
            for (var x = rect.Left + 2; x < rect.Right + 2; x += 2, up = !up)
                ctx.LineTo(new Point(Math.Min(x, rect.Right), up ? y - 2 : y));
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, Squiggle, geometry);
    }
}
