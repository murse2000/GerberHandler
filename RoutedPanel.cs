using System.IO;
using Clipper2Lib;

namespace GerberHandler;

internal sealed record RoutedPanel(IReadOnlyList<CamLine> Profile, IReadOnlyList<CamPoint> Bites, int TabCount)
{
    private const decimal UnitsPerMm = 1_000_000m;
    private const long ArcTolerance = 5_000;

    internal static RoutedPanel Build(CamInput input, PanelOptions options, RoutingOptions dimensions, CamBounds boards, CamBounds panel)
    {
        var source = input.Bounds;
        var channels = new List<decimal> { dimensions.RailChannel };
        if (options.Columns > 1) channels.Add(options.ColumnGap);
        if (options.Rows > 1) channels.Add(options.RowGap);
        if (dimensions.CutterDiameter <= 0 || channels.Any(gap => gap <= dimensions.CutterDiameter))
            throw new InvalidDataException("보드 사이 및 외곽 레일의 가공 간격을 라우터 공구 지름보다 크게 입력하세요.");
        if (new[] { options.RailLeft, options.RailRight, options.RailBottom, options.RailTop }.Any(width => width <= 0))
            throw new InvalidDataException("탭 라우팅에는 네 방향의 가이드 레일이 필요합니다.");
        if (dimensions.TabsPerEdge is < 1 or > 10 || dimensions.TabWidth <= 0 || dimensions.BiteDiameter <= 0 ||
            dimensions.BitePitch <= dimensions.BiteDiameter ||
            (dimensions.TabWidth / 2 - dimensions.BiteDiameter) / dimensions.BitePitch is < 1 or >= 13)
            throw new InvalidDataException("탭 개수, 탭 폭, 마우스바이트 지름과 간격을 확인하세요. 홀 한 줄에는 3~25개를 배치할 수 있습니다.");
        if (new[] { dimensions.RailChannel, dimensions.CutterDiameter, dimensions.TabWidth, dimensions.BiteDiameter, dimensions.BitePitch }
            .Any(value => value > 1000 || value != decimal.Round(value, 6)))
            throw new InvalidDataException("탭 라우팅 치수는 1000 mm 이하, 소수점 여섯 자리까지 입력하세요.");

        var radius = dimensions.BiteDiameter / 2;
        var offset = radius + options.CutClearance;
        if (dimensions.RailChannel <= offset + radius || channels.Skip(1).Any(gap => gap - 2 * offset <= dimensions.BiteDiameter))
            throw new InvalidDataException("두 보드 쪽 마우스바이트가 겹치지 않도록 가공 간격을 늘리세요.");

        var outline = CamEngine.Geometry(input.Files.Single(file => file.Role.StartsWith("Profile", StringComparison.Ordinal)));
        var contours = CamEngine.OutlineContours(outline);
        if (contours.Count != 1)
            throw new InvalidDataException("탭 라우팅은 외곽이 하나인 PCB를 지원합니다. 내부 컷아웃은 별도 가공이 필요합니다.");
        var shape = Positive(new Path64(contours[0].SkipLast(1).Select(Scale)));
        var material = new Paths64();
        var bites = new List<CamPoint>();
        var tabs = 0;
        var rail = CamEngine.Expand(boards, dimensions.RailChannel);
        material.Add(Rectangle(panel.MinX, panel.MinY, rail.MinX, panel.MaxY));
        material.Add(Rectangle(rail.MaxX, panel.MinY, panel.MaxX, panel.MaxY));
        material.Add(Rectangle(rail.MinX, panel.MinY, rail.MaxX, rail.MinY));
        material.Add(Rectangle(rail.MinX, rail.MaxY, rail.MaxX, panel.MaxY));

        var left = EdgeRanges(vertical: true, source.MinX);
        var right = EdgeRanges(vertical: true, source.MaxX);
        var bottom = EdgeRanges(vertical: false, source.MinY);
        var top = EdgeRanges(vertical: false, source.MaxY);
        var betweenColumns = Common(left, right);
        var betweenRows = Common(bottom, top);
        for (var row = 0; row < options.Rows; row++)
            for (var column = 0; column < options.Columns; column++)
            {
                var dx = column * options.PitchX(source);
                var dy = row * options.PitchY(source);
                material.Add(Clipper.TranslatePath(shape, Scale(dx), Scale(dy)));
                if (column == 0) AddTabs(true, rail.MinX, source.MinX, dy, left, false, true);
                if (column + 1 == options.Columns) AddTabs(true, source.MaxX + dx, rail.MaxX, dy, right, true, false);
                else AddTabs(true, source.MaxX + dx, source.MinX + dx + options.PitchX(source), dy, betweenColumns, true, true);
                if (row == 0) AddTabs(false, rail.MinY, source.MinY, dx, bottom, false, true);
                if (row + 1 == options.Rows) AddTabs(false, source.MaxY + dy, rail.MaxY, dx, top, true, false);
                else AddTabs(false, source.MaxY + dy, source.MinY + dy + options.PitchY(source), dx, betweenRows, true, true);
            }

        var outer = new Paths64 { Rectangle(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY) };
        var desiredOpenings = Clipper.Difference(outer, Clipper.Union(material, FillRule.NonZero), FillRule.NonZero);
        var cutterRadius = (double)(dimensions.CutterDiameter * UnitsPerMm / 2);
        // 공구 중심이 지나갈 수 있는 영역을 구해 탭 옆의 가공 불가능한 날카로운 모서리를 둥글린다.
        var toolPaths = Clipper.InflatePaths(desiredOpenings, -cutterRadius, JoinType.Round, EndType.Polygon, 2, ArcTolerance);
        var openings = Clipper.Intersect(desiredOpenings,
            Clipper.InflatePaths(toolPaths, cutterRadius, JoinType.Round, EndType.Polygon, 2, ArcTolerance), FillRule.NonZero);
        if (desiredOpenings.Count == 0 || toolPaths.Count != desiredOpenings.Count || openings.Count != desiredOpenings.Count)
            throw new InvalidDataException("설정한 공구로 가공할 수 없는 통로가 있습니다. 가공 간격이나 탭 위치 조건을 확인하세요.");
        var result = Clipper.Difference(outer, openings, FillRule.NonZero);
        if (result.Count(path => Clipper.IsPositive(path)) != 1)
            throw new InvalidDataException("라우팅 후 패널이 한 장으로 연결되지 않습니다.");
        var holeClearance = Clipper.InflatePaths(openings, (double)(radius * UnitsPerMm) + ArcTolerance,
            JoinType.Round, EndType.Polygon, 2, ArcTolerance);
        if (bites.Any(point => holeClearance.Any(path => Clipper.PointInPolygon(Scale(point), path) != PointInPolygonResult.IsOutside)))
            throw new InvalidDataException("마우스바이트가 라우팅 개구와 겹칩니다. 탭 폭과 홀 지름을 확인하세요.");
        var lines = result.SelectMany(path => path.Select((point, index) =>
            new CamLine(Unscale(point), Unscale(path[(index + 1) % path.Count])))).ToArray();
        return new RoutedPanel(lines, bites, tabs);

        (decimal Start, decimal End)[] EdgeRanges(bool vertical, decimal axis) => outline
            .Where(line => line.Kind == "Line" && line.Points.All(point => (vertical ? point.X : point.Y) == axis))
            .Select(line => (line.Points.Min(p => vertical ? p.Y : p.X), line.Points.Max(p => vertical ? p.Y : p.X)))
            .ToArray();

        void AddTabs(bool vertical, decimal start, decimal end, decimal move, (decimal Start, decimal End)[] ranges, bool firstBoard, bool secondBoard)
        {
            var reserve = dimensions.TabWidth / 2 + dimensions.CutterDiameter;
            var run = ranges.Where(range => range.End - range.Start >= 2 * reserve +
                (dimensions.TabsPerEdge - 1) * (dimensions.TabWidth + dimensions.CutterDiameter))
                .OrderByDescending(range => range.End - range.Start).FirstOrDefault();
            if (run.End <= run.Start)
                throw new InvalidDataException("보드의 마주 보는 직선 외곽에 탭을 배치할 공간이 없습니다. 탭 폭이나 개수를 줄이세요.");
            for (var index = 0; index < dimensions.TabsPerEdge; index++)
            {
                var center = move + (dimensions.TabsPerEdge == 1 ? (run.Start + run.End) / 2
                    : run.Start + reserve + index * (run.End - run.Start - 2 * reserve) / (dimensions.TabsPerEdge - 1));
                var half = dimensions.TabWidth / 2;
                // 소량 겹쳐 결합한 뒤 합집합으로 보드와 탭을 하나의 외곽으로 만든다.
                var overlap = Math.Min(dimensions.CutterDiameter / 4, .2m);
                material.Add(vertical ? Rectangle(start - overlap, center - half, end + overlap, center + half)
                    : Rectangle(center - half, start - overlap, center + half, end + overlap));
                if (firstBoard) AddBites(start + offset);
                if (secondBoard) AddBites(end - offset);
                tabs++;

                void AddBites(decimal axis)
                {
                    var count = (int)((half - dimensions.BiteDiameter) / dimensions.BitePitch);
                    for (var hole = -count; hole <= count; hole++)
                    {
                        var position = center + hole * dimensions.BitePitch;
                        bites.Add(Unscale(Scale(vertical ? new CamPoint(axis, position) : new CamPoint(position, axis))));
                    }
                }
            }
        }
    }

    private static (decimal Start, decimal End)[] Common((decimal Start, decimal End)[] first, (decimal Start, decimal End)[] second) =>
        (from a in first from b in second select (Math.Max(a.Start, b.Start), Math.Min(a.End, b.End))).ToArray();

    private static Path64 Positive(Path64 path)
    {
        if (!Clipper.IsPositive(path)) path.Reverse();
        return path;
    }

    private static Path64 Rectangle(decimal x1, decimal y1, decimal x2, decimal y2) =>
        [Scale(new CamPoint(x1, y1)), Scale(new CamPoint(x2, y1)), Scale(new CamPoint(x2, y2)), Scale(new CamPoint(x1, y2))];

    private static Point64 Scale(CamPoint point) => new(Scale(point.X), Scale(point.Y));
    private static long Scale(decimal value) => (long)decimal.Round(value * UnitsPerMm);
    private static CamPoint Unscale(Point64 point) => new(point.X / UnitsPerMm, point.Y / UnitsPerMm);
}
