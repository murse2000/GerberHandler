using System.IO;
using Clipper2Lib;

namespace GerberHandler;

internal static class MixedRouting
{
    private readonly record struct Interval(decimal Start, decimal End);
    private sealed record Part(CamBounds Bounds, Interval[][] Sides, bool IsRail);
    private sealed record Bridge(int First, int Second, bool Vertical, decimal Start, decimal End, Interval[] Span);

    internal static RoutedPanel Build(IReadOnlyList<MixedBoard> boards, PanelOptions options, RoutingOptions routing,
        CamBounds boardBounds, CamBounds panelBounds)
    {
        var radius = routing.BiteDiameter / 2;
        var offset = radius + options.CutClearance;
        if (routing.CutterDiameter <= 0 || routing.TabWidth <= 0 || routing.BiteDiameter <= 0 ||
            routing.TabsPerEdge is < 1 or > 10 || routing.BitePitch <= routing.BiteDiameter ||
            (routing.TabWidth / 2 - routing.BiteDiameter) / routing.BitePitch is < 1 or >= 13 ||
            new[] { routing.CutterDiameter, routing.TabWidth, routing.BiteDiameter, routing.BitePitch, routing.RailChannel }
                .Any(value => value < 0 || value > 1000 || decimal.Round(value, 6) != value))
            throw new InvalidDataException("탭 라우팅의 공구, 탭 및 마우스바이트 치수를 확인하세요.");

        var rail = CamEngine.RailLimit(boardBounds, options);
        if (rail != boardBounds && (routing.RailChannel <= routing.CutterDiameter || routing.RailChannel <= offset + radius))
            throw new InvalidDataException("보드와 가이드 레일 사이에 공구 및 마우스바이트를 위한 공간이 필요합니다.");

        var material = new Paths64();
        var cutouts = new Paths64();
        var parts = new List<Part>();
        foreach (var board in boards)
        {
            var bbox = board.Bounds;
            foreach (var other in parts)
            {
                var dx = Math.Max(0, Math.Max(other.Bounds.MinX - bbox.MaxX, bbox.MinX - other.Bounds.MaxX));
                var dy = Math.Max(0, Math.Max(other.Bounds.MinY - bbox.MaxY, bbox.MinY - other.Bounds.MaxY));
                if (dx * dx + dy * dy <= routing.CutterDiameter * routing.CutterDiameter)
                    throw new InvalidDataException("보드 간격을 라우터 공구 지름보다 크게 배치하세요.");
            }

            var outline = CamEngine.Geometry(board.Input.Files.Single(file => file.Role.StartsWith("Profile", StringComparison.Ordinal)))
                .Select(board.Transform).ToArray();
            var (outer, boardCutouts) = RoutedPanel.SplitProfile(outline);
            material.Add(outer);
            cutouts.AddRange(boardCutouts);
            parts.Add(new(bbox, [Side(true, bbox.MinX), Side(true, bbox.MaxX), Side(false, bbox.MinY), Side(false, bbox.MaxY)], false));

            Interval[] Side(bool vertical, decimal axis) => outline
                .Where(line => line.Kind == "Line" && line.Points.All(p => (vertical ? p.X : p.Y) == axis))
                .Select(line => new Interval(line.Points.Min(p => vertical ? p.Y : p.X), line.Points.Max(p => vertical ? p.Y : p.X)))
                .ToArray();
        }

        AddRail(new CamBounds(panelBounds.MinX, panelBounds.MinY, rail.MinX, panelBounds.MaxY), 1);
        AddRail(new CamBounds(rail.MaxX, panelBounds.MinY, panelBounds.MaxX, panelBounds.MaxY), 0);
        AddRail(new CamBounds(rail.MinX, panelBounds.MinY, rail.MaxX, rail.MinY), 3);
        AddRail(new CamBounds(rail.MinX, rail.MaxY, rail.MaxX, panelBounds.MaxY), 2);

        var candidates = new List<Bridge>();
        for (var first = 0; first < parts.Count; first++)
            for (var second = 0; second < parts.Count; second++)
            {
                if (first == second)
                    continue;
                var a = parts[first];
                var b = parts[second];
                if (a.IsRail && b.IsRail)
                    continue;
                Consider(true, a.Bounds.MaxX, b.Bounds.MinX, a.Sides[1], b.Sides[0]);
                Consider(false, a.Bounds.MaxY, b.Bounds.MinY, a.Sides[3], b.Sides[2]);

                void Consider(bool vertical, decimal start, decimal end, Interval[] aSpans, Interval[] bSpans)
                {
                    var gap = end - start;
                    if (gap <= routing.CutterDiameter || gap <= (a.IsRail || b.IsRail ? offset + radius : 2 * offset + routing.BiteDiameter))
                        return;
                    var spans = (from aSpan in aSpans from bSpan in bSpans
                        select new Interval(Math.Max(aSpan.Start, bSpan.Start), Math.Min(aSpan.End, bSpan.End)))
                        .Where(span => span.End > span.Start).ToArray();
                    if (spans.Length > 0)
                        candidates.Add(new(first, second, vertical, start, end, spans));
                }
            }

        var tabAreas = new List<CamBounds>();
        var bites = new List<CamPoint>();
        foreach (var bridge in candidates.OrderBy(item => item.End - item.Start))
        {
            var spans = bridge.Span.ToList();
            foreach (var blocker in parts.Where((_, index) => index != bridge.First && index != bridge.Second)
                         .Select(part => part.Bounds).Concat(tabAreas))
            {
                var start = bridge.Vertical ? blocker.MinX : blocker.MinY;
                var end = bridge.Vertical ? blocker.MaxX : blocker.MaxY;
                if (start >= bridge.End + routing.CutterDiameter / 2 || end <= bridge.Start - routing.CutterDiameter / 2)
                    continue;
                var left = bridge.Vertical ? blocker.MinY : blocker.MinX;
                var right = bridge.Vertical ? blocker.MaxY : blocker.MaxX;
                // 가공을 방해하는 다른 보드나 탭이 통로에 닿으면 해당 구간은 사용하지 않는다.
                spans = spans.SelectMany(span => new[] { new Interval(span.Start, Math.Min(span.End, left)),
                        new Interval(Math.Max(span.Start, right), span.End) })
                    .Where(span => span.End > span.Start).ToList();
            }

            var half = routing.TabWidth / 2;
            var reserve = half + routing.CutterDiameter;
            var run = spans.Where(s => s.End - s.Start >= 2 * reserve + (routing.TabsPerEdge - 1) *
                    (routing.TabWidth + routing.CutterDiameter))
                .OrderByDescending(s => s.End - s.Start).FirstOrDefault();
            if (run.End <= run.Start)
                continue;

            var a = parts[bridge.First].Bounds;
            var b = parts[bridge.Second].Bounds;
            var depth = bridge.Vertical ? Math.Min(a.Width, b.Width) : Math.Min(a.Height, b.Height);
            var overlap = Math.Min(.2m, Math.Min(routing.CutterDiameter, depth) / 4);
            for (var tab = 0; tab < routing.TabsPerEdge; tab++)
            {
                var center = routing.TabsPerEdge == 1 ? (run.Start + run.End) / 2 : run.Start + reserve +
                    tab * (run.End - run.Start - 2 * reserve) / (routing.TabsPerEdge - 1);
                var area = bridge.Vertical ? new CamBounds(bridge.Start - overlap, center - half, bridge.End + overlap, center + half)
                    : new CamBounds(center - half, bridge.Start - overlap, center + half, bridge.End + overlap);
                tabAreas.Add(area);
                material.Add(RoutedPanel.Rectangle(area.MinX, area.MinY, area.MaxX, area.MaxY));
                if (!parts[bridge.First].IsRail) AddBites(bridge.Start + offset);
                if (!parts[bridge.Second].IsRail) AddBites(bridge.End - offset);

                void AddBites(decimal axis)
                {
                    var count = (int)((half - routing.BiteDiameter) / routing.BitePitch);
                    for (var hole = -count; hole <= count; hole++)
                    {
                        var pos = center + hole * routing.BitePitch;
                        bites.Add(bridge.Vertical ? new(decimal.Round(axis, 6), decimal.Round(pos, 6))
                            : new(decimal.Round(pos, 6), decimal.Round(axis, 6)));
                    }
                }
            }
        }

        if (Clipper.Union(material, FillRule.NonZero).Count(Clipper.IsPositive) != 1)
            throw new InvalidDataException("모든 보드와 레일을 연결할 탭 공간이 없습니다. 마주 보는 직선 외곽을 맞추거나 탭 폭과 개수를 조정하세요.");
        return RoutedPanel.Finish(material, cutouts, bites, tabAreas.Count, panelBounds, routing);

        void AddRail(CamBounds bounds, int face)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;
            Interval[][] sides = [[], [], [], []];
            sides[face] = face is 0 or 1 ? [new(bounds.MinY, bounds.MaxY)] : [new(bounds.MinX, bounds.MaxX)];
            parts.Add(new(bounds, sides, true));
            material.Add(RoutedPanel.Rectangle(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY));
        }
    }
}
