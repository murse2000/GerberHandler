using System.Globalization;

namespace GerberHandler;

internal sealed record PanelFinding(string Text, CamPoint? Location);

internal sealed record ComparisonResult(IReadOnlyList<CamShape> Missing, IReadOnlyList<CamShape> Added, IReadOnlyList<PanelFinding> Findings);

internal static class PanelComparison
{
    private const decimal Tolerance = 0.000001m;

    public static ComparisonResult Compare(CamInput original, CamInput output, int row, int column, PanelOptions options)
    {
        var missing = new List<CamShape>();
        var added = new List<CamShape>();
        var findings = new List<PanelFinding>();
        var deltaX = column * options.PitchX(original.Bounds);
        var deltaY = row * options.PitchY(original.Bounds);
        var left = original.Bounds.MinX + deltaX;
        var right = original.Bounds.MaxX + deltaX;
        var bottom = original.Bounds.MinY + deltaY;
        var top = original.Bounds.MaxY + deltaY;

        var originals = original.Files.Where(IsBoardLayer).ToLookup(file => file.Role);
        var outputs = output.Files.Where(IsBoardLayer).ToLookup(file => file.Role);
        foreach (var role in originals.Select(group => group.Key).Union(outputs.Select(group => group.Key)))
        {
            var expected = originals[role].SelectMany(CamEngine.Geometry).Select(shape => Move(shape, deltaX, deltaY));
            var actual = outputs[role].SelectMany(CamEngine.Geometry).Where(shape => Owns(shape.Points));
            var byShape = actual.ToLookup(Key).ToDictionary(group => group.Key, group => new Queue<CamShape>(group));
            foreach (var shape in expected)
            {
                if (byShape.TryGetValue(Key(shape), out var matches) && matches.TryDequeue(out _))
                    continue;
                missing.Add(shape);
                findings.Add(new PanelFinding($"누락 또는 이동 · {role} · {shape.Kind}", shape.Points.FirstOrDefault()));
            }

            foreach (var shape in byShape.Values.SelectMany(queue => queue))
            {
                added.Add(shape);
                findings.Add(new PanelFinding($"추가 또는 이동 · {role} · {shape.Kind}", shape.Points.FirstOrDefault()));
            }
        }

        if (missing.Count + added.Count == 0)
            findings.Add(new("선택한 셀의 원본 형상이 일치합니다.", null));

        var rails = new[] { options.RailLeft, options.RailRight, options.RailBottom, options.RailTop };
        if (rails.Any(rail => rail > 0) || options.Rows * options.Columns > 1)
            findings.Add(new("계획된 변경 · 패널 외곽 및 배열 분리선", new CamPoint(output.Bounds.MinX, output.Bounds.MinY)));
        foreach (var point in output.Files.Where(file => file.Role.StartsWith("Copper,", StringComparison.Ordinal))
                     .SelectMany(CamEngine.Geometry).Where(shape => shape.IsFiducial && !InBoardArea(shape.Points[0])))
            findings.Add(new("계획된 변경 · 레일 피두셜", point.Points[0]));
        foreach (var shape in output.Files.Where(file => file.IsDrill && file.Role.StartsWith("NonPlated,", StringComparison.Ordinal))
                     .SelectMany(CamEngine.Geometry).Where(shape => !InBoardArea(shape.Points[0])))
            findings.Add(new("배열 외곽의 비도금 홀 · 위치 확인", shape.Points[0]));

        return new(missing, added, findings);

        bool InBoardArea(CamPoint point) => point.X >= original.Bounds.MinX && point.Y >= original.Bounds.MinY &&
            point.X <= original.Bounds.MaxX + (options.Columns - 1) * options.PitchX(original.Bounds) &&
            point.Y <= original.Bounds.MaxY + (options.Rows - 1) * options.PitchY(original.Bounds);

        bool Owns(IReadOnlyList<CamPoint> points)
        {
            if (points.Count == 0)
                return false;
            var x = (points.Min(p => p.X) + points.Max(p => p.X)) / 2;
            var y = (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2;
            return x >= left - Tolerance && y >= bottom - Tolerance &&
                (column == options.Columns - 1 || options.ColumnGap > 0 ? x <= right + Tolerance : x < right - Tolerance) &&
                (row == options.Rows - 1 || options.RowGap > 0 ? y <= top + Tolerance : y < top - Tolerance);
        }
    }

    private static bool IsBoardLayer(CamFile file) => file.IsDrill ||
        !file.Role.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) &&
        !file.Role.StartsWith("Vcut", StringComparison.OrdinalIgnoreCase);

    private static CamShape Move(CamShape shape, decimal x, decimal y) => shape with
    {
        Points = shape.Points.Select(point => new CamPoint(point.X + x, point.Y + y)).ToArray(),
        Contours = shape.Contours?.Select(contour => (IReadOnlyList<CamPoint>)contour
            .Select(point => new CamPoint(point.X + x, point.Y + y)).ToArray()).ToArray()
    };

    private static string Key(CamShape shape) => $"{shape.Kind}|{shape.Dark}|{shape.IsCircular}|{Number(shape.Width)}|{Number(shape.Height)}|" +
        Points(shape.Points) + "|" + string.Join("/", shape.Contours?.Select(Points) ?? []);

    private static string Points(IReadOnlyList<CamPoint> points) =>
        string.Join(";", points.Select(point => $"{Number(point.X)},{Number(point.Y)}"));

    private static string Number(decimal value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
