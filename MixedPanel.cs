using System.IO;

namespace GerberHandler;

internal sealed record MixedBoard(CamInput Input, CamPoint Origin)
{
    internal IReadOnlyDictionary<string, string>? RoleOverrides { get; init; }
    public string Name => Path.GetFileName(Input.SourcePath);
    public string Caption => $"{Name} · {Input.Bounds.Width:0.###} × {Input.Bounds.Height:0.###} mm";
    internal CamPoint Offset => new(Origin.X - Input.Bounds.MinX, Origin.Y - Input.Bounds.MinY);
    internal CamBounds Bounds => new(Origin.X, Origin.Y, Origin.X + Input.Bounds.Width, Origin.Y + Input.Bounds.Height);
}

internal static class MixedPanel
{
    internal const int MaxBoards = 100;
    internal const decimal DefaultGap = 5;

    internal static (CamBounds Boards, CamBounds Panel) Placement(IReadOnlyList<MixedBoard> boards, PanelOptions options)
    {
        if (boards.Count is < 1 or > MaxBoards || boards.Any(board => board.Input.IsPanel))
            throw new InvalidDataException($"단일 PCB 제조 세트를 {MaxBoards}장까지 배치할 수 있습니다.");
        if (new[] { options.RailLeft, options.RailRight, options.RailBottom, options.RailTop,
            options.Routing?.RailChannel ?? 0, options.CutClearance }.Any(value => value is < 0 or > 10000))
            throw new InvalidDataException("레일 폭과 가공 이격 치수를 확인하세요.");
        for (var i = 0; i < boards.Count; i++)
            for (var j = 0; j < i; j++)
                if (Overlaps(boards[i].Bounds, boards[j].Bounds))
                    throw new InvalidDataException($"보드가 겹칩니다. {boards[j].Name} / {boards[i].Name}");

        var bounds = new CamBounds(boards.Min(board => board.Bounds.MinX), boards.Min(board => board.Bounds.MinY),
            boards.Max(board => board.Bounds.MaxX), boards.Max(board => board.Bounds.MaxY));
        var frame = CamEngine.RailLimit(bounds, options);
        var panel = new CamBounds(frame.MinX - options.RailLeft, frame.MinY - options.RailBottom,
            frame.MaxX + options.RailRight, frame.MaxY + options.RailTop);
        if (new[] { panel.MinX, panel.MinY, panel.MaxX, panel.MaxY }.Any(value => Math.Abs(value) >= 10000m) ||
            panel.Width > 10000m || panel.Height > 10000m)
            throw new InvalidDataException("혼합 패널 좌표 또는 치수가 지원 범위를 초과했습니다.");
        return (bounds, panel);
    }

    internal static CamPanel Build(IReadOnlyList<MixedBoard> boards, PanelOptions options)
    {
        var routing = options.Routing ?? throw new InvalidDataException("혼합 패널 제조 ZIP에는 탭 라우팅을 선택하세요.");
        var (bounds, panel) = Placement(boards, options);
        if (boards.Sum(board => board.Input.Files.Sum(file => (long)file.Text.Length)) > 128 * 1024 * 1024)
            throw new InvalidDataException("혼합 패널의 원본 데이터 크기가 지원 범위를 초과했습니다.");
        foreach (var board in boards)
        {
            if (board.Input.Files.Any(file => file.Role == "Vcut" || file.Role == "Profile,P"))
                throw new InvalidDataException($"'{board.Name}'의 기존 절단선 또는 도금 외곽은 혼합 라우팅에서 지원하지 않습니다.");
            foreach (var file in board.Input.Files.Where(file => !file.Role.StartsWith("Profile", StringComparison.Ordinal)))
                foreach (var shape in CamEngine.Geometry(file))
                {
                    var extent = CamEngine.ShapeBounds(shape);
                    var placed = new CamBounds(extent.MinX + board.Offset.X, extent.MinY + board.Offset.Y,
                        extent.MaxX + board.Offset.X, extent.MaxY + board.Offset.Y);
                    if ((file.IsDrill || file.Role.StartsWith("Copper,", StringComparison.Ordinal) || !shape.Dark) &&
                        (placed.MinX < board.Bounds.MinX || placed.MinY < board.Bounds.MinY ||
                         placed.MaxX > board.Bounds.MaxX || placed.MaxY > board.Bounds.MaxY))
                        throw new InvalidDataException($"'{board.Name}'의 동박·홀·지움 형상이 보드 경계를 벗어납니다. {file.Name}");
                    if (boards.Any(other => !ReferenceEquals(other, board) && Overlaps(placed, other.Bounds)))
                        throw new InvalidDataException($"'{board.Name}'의 {file.Name} 형상이 다른 보드 위치까지 돌출됩니다.");
                }
        }

        var route = MixedRouting.Build(boards, options, routing, bounds, panel);
        var (fiducials, tooling) = RailMarks(options, bounds, panel);
        var files = MixedCamFiles.Merge(boards, options, fiducials, tooling, route.Bites).ToList();
        files.Add(CamEngine.LineFile("panel-Profile.gbr", "Profile,NP", route.Profile));
        var warnings = boards.Where(board => !board.Input.Files.Any(file => file.IsDrill))
            .Select(board => $"'{board.Name}'에 원본 드릴 파일이 없습니다.").Distinct().ToArray();
        return new CamPanel(files, panel, [], fiducials, tooling, warnings) { Tabs = route.TabCount, MouseBites = route.Bites.Count };
    }

    internal static (IReadOnlyList<CamPoint> Fiducials, IReadOnlyList<CamPoint> Tooling) RailMarks(
        PanelOptions options, CamBounds bounds, CamBounds panel)
    {
        var frame = CamEngine.RailLimit(bounds, options);
        var cuts = new List<CamLine>();
        if (options.RailLeft > 0) cuts.Add(new(new(frame.MinX, panel.MinY), new(frame.MinX, panel.MaxY)));
        if (options.RailRight > 0) cuts.Add(new(new(frame.MaxX, panel.MinY), new(frame.MaxX, panel.MaxY)));
        if (options.RailBottom > 0) cuts.Add(new(new(panel.MinX, frame.MinY), new(panel.MaxX, frame.MinY)));
        if (options.RailTop > 0) cuts.Add(new(new(panel.MinX, frame.MaxY), new(panel.MaxX, frame.MaxY)));
        var used = new List<(CamPoint Point, decimal Radius)>();
        if (options.Fiducials && (options.FiducialDiameter <= 0 || options.MaskDiameter <= options.FiducialDiameter) ||
            options.Tooling && options.ToolingDiameter <= 0)
            throw new InvalidDataException("피두셜 또는 고정 홀 치수를 확인하세요.");
        var fiducials = options.Fiducials ? CamEngine.Marks(panel, frame, cuts, options.CutClearance, options.MaskDiameter / 2,
            options.FiducialPositions, used) : [];
        var tooling = options.Tooling ? CamEngine.Marks(panel, frame, cuts, options.CutClearance, options.ToolingDiameter / 2,
            options.ToolingPositions, used) : [];
        return (fiducials, tooling);
    }

    private static bool Overlaps(CamBounds a, CamBounds b) => a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinY < b.MaxY && b.MinY < a.MaxY;
}
