using System.IO;

namespace GerberHandler;

internal sealed record MixedBoard(CamInput Input, CamPoint Origin)
{
    internal IReadOnlyDictionary<string, string>? RoleOverrides { get; init; }
    internal int QuarterTurns { get; init; }
    public string Name => Path.GetFileName(Input.SourcePath);
    public string Caption => $"{Name} · {Bounds.Width:0.###} × {Bounds.Height:0.###} mm · {QuarterTurns * 90}°";
    private bool SwapsAxes => QuarterTurns % 2 != 0;
    internal CamBounds Bounds => new(Origin.X, Origin.Y, Origin.X + (SwapsAxes ? Input.Bounds.Height : Input.Bounds.Width),
        Origin.Y + (SwapsAxes ? Input.Bounds.Width : Input.Bounds.Height));

    internal CamPoint RotateVector(CamPoint vector) => QuarterTurns switch
    {
        1 => new(vector.Y, -vector.X),
        2 => new(-vector.X, -vector.Y),
        3 => new(-vector.Y, vector.X),
        _ => vector
    };

    internal CamPoint Transform(CamPoint point)
    {
        var relative = RotateVector(new(point.X - Input.Bounds.MinX, point.Y - Input.Bounds.MinY));
        // 회전 전후 배치의 좌하단 기준점은 유지한다.
        return QuarterTurns switch
        {
            1 => new(Origin.X + relative.X, Origin.Y + Input.Bounds.Width + relative.Y),
            2 => new(Origin.X + Input.Bounds.Width + relative.X, Origin.Y + Input.Bounds.Height + relative.Y),
            3 => new(Origin.X + Input.Bounds.Height + relative.X, Origin.Y + relative.Y),
            _ => new(Origin.X + relative.X, Origin.Y + relative.Y)
        };
    }

    internal CamShape Transform(CamShape shape) => shape with
    {
        Points = shape.Points.Select(Transform).ToArray(),
        Contours = shape.Contours?.Select(contour => (IReadOnlyList<CamPoint>)contour.Select(Transform).ToArray()).ToArray(),
        Width = SwapsAxes ? shape.Height : shape.Width,
        Height = SwapsAxes ? shape.Width : shape.Height
    };
}

internal static class MixedPanel
{
    internal const int MaxBoards = 100;
    internal const decimal DefaultGap = 5;

    internal static IReadOnlyList<MixedBoard> AutoArrange(IReadOnlyList<MixedBoard> boards, decimal gap, PanelOptions options, Profile? profile)
    {
        if (options.Routing is { } routing && gap <= routing.CutterDiameter)
            throw new InvalidDataException("탭 라우팅 자동 배치에는 자동 정렬 간격을 라우터 공구 지름보다 크게 입력하세요.");

        var origin = new CamPoint(boards.Min(board => board.Origin.X), boards.Min(board => board.Origin.Y));
        var candidates = new List<(MixedBoard[] Boards, CamBounds Content, CamBounds Panel)>();
        foreach (var horizontal in new[] { true, false })
        {
            var indexed = boards.Select((board, index) => (board, index)).ToArray();
            var sorted = indexed.OrderByDescending(item => horizontal ? item.board.Bounds.Height : item.board.Bounds.Width);
            foreach (var order in new[] { indexed, sorted.ToArray() })
                for (var perShelf = 1; perShelf <= boards.Count; perShelf++)
                {
                    var placed = new MixedBoard[boards.Count];
                    var cross = 0m;
                    foreach (var shelf in order.Chunk(perShelf))
                    {
                        var along = 0m;
                        foreach (var (board, index) in shelf)
                        {
                            var point = horizontal ? new CamPoint(origin.X + along, origin.Y + cross)
                                : new CamPoint(origin.X + cross, origin.Y + along);
                            placed[index] = board with { Origin = point };
                            along += (horizontal ? board.Bounds.Width : board.Bounds.Height) + gap;
                        }
                        cross += shelf.Max(item => horizontal ? item.board.Bounds.Height : item.board.Bounds.Width) + gap;
                    }
                    try
                    {
                        var (content, panel) = Placement(placed, options);
                        candidates.Add((placed, content, panel));
                    }
                    catch (InvalidDataException)
                    {
                        // 후보 하나가 지원 좌표 범위를 넘겨도 다른 행·열 조합은 계속 확인한다.
                    }
                }
        }
        var ranked = candidates.OrderByDescending(item => FitsProfile(item.Panel))
            .ThenBy(item => item.Panel.Width * item.Panel.Height).ThenBy(item => Math.Max(item.Panel.Width, item.Panel.Height)).ToArray();
        if (ranked.Length == 0)
            throw new InvalidDataException("지원 범위 안에 자동으로 배치할 수 없습니다. 보드 수나 간격을 줄이세요.");
        if (options.Routing is { } enabled)
            foreach (var candidate in ranked.TakeWhile(item => FitsProfile(item.Panel) == FitsProfile(ranked[0].Panel))
                         .DistinctBy(item => string.Join("|", item.Boards.Select(board => board.Origin))).Take(6))
            {
                try
                {
                    _ = MixedRouting.Build(candidate.Boards, options with { MixedTabs = null }, enabled, candidate.Content, candidate.Panel);
                    return candidate.Boards;
                }
                catch (InvalidDataException)
                {
                    // 탭을 연결할 수 없는 배치 후보는 건너뛴다.
                }
            }
        return ranked[0].Boards;

        bool FitsProfile(CamBounds bounds) => profile is null || bounds.Width >= profile.MinWidth && bounds.Width <= profile.MaxWidth &&
            bounds.Height >= profile.MinHeight && bounds.Height <= profile.MaxHeight;
    }

    internal static (CamBounds Boards, CamBounds Panel) Placement(IReadOnlyList<MixedBoard> boards, PanelOptions options)
    {
        if (boards.Count is < 1 or > MaxBoards || boards.Any(board => board.Input.IsPanel))
            throw new InvalidDataException($"단일 PCB 제조 세트를 {MaxBoards}장까지 배치할 수 있습니다.");
        if (boards.Any(board => board.QuarterTurns is < 0 or > 3))
            throw new InvalidDataException("저장된 보드 회전 값이 잘못되었습니다.");
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

    internal static CamPanel Build(IReadOnlyList<MixedBoard> boards, PanelOptions options) => BuildWithRoute(boards, options, null);

    internal static CamPanel BuildWithRoute(IReadOnlyList<MixedBoard> boards, PanelOptions options, RoutedPanel? preparedRoute)
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
                    var placed = CamEngine.ShapeBounds(board.Transform(shape));
                    if ((file.IsDrill || file.Role.StartsWith("Copper,", StringComparison.Ordinal) || !shape.Dark) &&
                        (placed.MinX < board.Bounds.MinX || placed.MinY < board.Bounds.MinY ||
                         placed.MaxX > board.Bounds.MaxX || placed.MaxY > board.Bounds.MaxY))
                        throw new InvalidDataException($"'{board.Name}'의 동박·홀·지움 형상이 보드 경계를 벗어납니다. {file.Name}");
                    if (boards.Any(other => !ReferenceEquals(other, board) && Overlaps(placed, other.Bounds)))
                        throw new InvalidDataException($"'{board.Name}'의 {file.Name} 형상이 다른 보드 위치까지 돌출됩니다.");
                }
        }

        var route = preparedRoute ?? MixedRouting.Build(boards, options, routing, bounds, panel);
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
