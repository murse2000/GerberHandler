using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace GerberHandler;

public readonly record struct CamPoint(decimal X, decimal Y);
public readonly record struct CamLine(CamPoint From, CamPoint To);
public readonly record struct CamBounds(decimal MinX, decimal MinY, decimal MaxX, decimal MaxY)
{
    public decimal Width => MaxX - MinX;
    public decimal Height => MaxY - MinY;
}

public sealed record CamFile(string Name, string Role, string Text, bool IsDrill);
public sealed record CamInput(string SourcePath, IReadOnlyList<CamFile> Files, CamBounds Bounds, bool IsPanel);
public sealed record CamShape(string Kind, IReadOnlyList<CamPoint> Points, decimal Width, decimal Height,
    bool Dark, bool IsCircular = false, bool IsFiducial = false,
    IReadOnlyList<IReadOnlyList<CamPoint>>? Contours = null);

public sealed record PanelOptions(int Rows, int Columns, decimal RailLeft, decimal RailRight,
    decimal RailBottom, decimal RailTop, bool Fiducials, decimal FiducialDiameter, decimal MaskDiameter,
    bool Tooling, decimal ToolingDiameter, decimal CutClearance = 0m)
{
    public IReadOnlyList<CamPoint>? FiducialPositions { get; init; }
    public IReadOnlyList<CamPoint>? ToolingPositions { get; init; }
}

public sealed record CamPanel(IReadOnlyList<CamFile> Files, CamBounds Bounds, IReadOnlyList<CamLine> Cuts,
    IReadOnlyList<CamPoint> Fiducials, IReadOnlyList<CamPoint> Tooling, IReadOnlyList<string> Warnings);

public static class CamEngine
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Command = new(@"\G\s*(%[^%]+%|[^%*]+\*)", RegexOptions.CultureInvariant);
    private static readonly Regex GerberFormat = new(@"^%FSLAX([1-6])([1-7])Y([1-6])([1-7])\*%$", RegexOptions.CultureInvariant);
    private static readonly Regex Aperture = new(@"^%ADD(\d+)([A-Za-z_][A-Za-z0-9_.-]*)(?:,([^*]+))?\*%$", RegexOptions.CultureInvariant);
    private static readonly Regex Operation = new(@"^(?:(G0[123]))?(?:X([+-]?\d+))?(?:Y([+-]?\d+))?(?:I([+-]?\d+))?(?:J([+-]?\d+))?D0([123])\*$", RegexOptions.CultureInvariant);
    private static readonly Regex Repeat = new(@"^%SRX(\d+)Y(\d+)I([\d.]+)J([\d.]+)\*%$", RegexOptions.CultureInvariant);
    private static readonly Regex Tool = new(@"^T(\d+)C([\d.]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Hit = new(@"^X([+-]?\d+(?:\.\d+)?)Y([+-]?\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant);
    private static readonly Regex Coord = new(@"([XY])([+-]?\d+)", RegexOptions.CultureInvariant);
    private const int MaxCopies = 100;
    private const int MaxFileBytes = 32 * 1024 * 1024;
    private const long MaxSetBytes = 128 * 1024 * 1024;
    private const long MaxGeneratedBytes = 128 * 1024 * 1024;
    private const decimal ArcTolerance = .0001m;

    public static CamInput Load(string path, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var files = new List<CamFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        void Add(string name, long length, Func<Stream> open)
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            var drill = extension == ".drl";
            if (!drill && !Regex.IsMatch(extension, @"^\.(gbr|gtl|gbl|g\d+|gt[ops]|gb[ops]|gm\d+|gko)$"))
                return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !names.Add(name))
                throw Bad($"제조 파일 이름이 잘못되었거나 중복됩니다. {name}");
            total += length;
            if (length > MaxFileBytes || total > MaxSetBytes)
                throw Bad("제조 파일 또는 파일 세트가 지원 크기를 초과했습니다.");

            using var stream = open();
            using var reader = new StreamReader(stream, Utf8);
            var source = reader.ReadToEnd();
            var role = drill
                ? Regex.Match(source, @"(?m)^;\s*#@!\s*TF\.FileFunction,([^\r\n]+)").Groups[1].Value.Trim()
                : GerberAttribute(source, "FileFunction");
            if (!drill && role == "Drillmap")
                return;
            var roleFromHeader = role;
            if (role.Length == 0 && !drill)
                role = extension switch
                {
                    ".gtl" => "Copper,L1,Top", ".gts" => "Soldermask,Top", ".gbs" => "Soldermask,Bot",
                    ".gtp" => "Paste,Top", ".gbp" => "Paste,Bot", ".gto" => "Legend,Top", ".gbo" => "Legend,Bot",
                    ".gm1" or ".gko" => "Profile,NP", _ => ""
                };
            if (overrides?.TryGetValue(name, out var mappedRole) == true)
            {
                if (roleFromHeader.Length > 0 && roleFromHeader != mappedRole)
                    throw Bad($"{name}: 수동 역할이 파일의 역할 정보와 일치하지 않습니다.");
                if (drill && Regex.Matches(source, @"(?m)^;\s*#@!\s*TA\.AperFunction,(Plated|NonPlated),")
                    .Any(match => !mappedRole.StartsWith(match.Groups[1].Value + ",", StringComparison.Ordinal)))
                    throw Bad($"{name}: 수동 도금 구분이 드릴 도구 정보와 일치하지 않습니다.");
                role = mappedRole;
            }
            if (drill && !ValidRole(role, true))
                throw Bad($"{name}: PTH와 NPTH를 KiCad에서 분리해 내보내세요. 관통 원형 드릴을 지원합니다.");
            if (!drill && !ValidRole(role, false))
                throw Bad($"{name}: 지원할 수 있는 레이어 역할을 파일에서 확인하지 못했습니다.");
            files.Add(new CamFile(name, role, source, drill));
        }

        if (Directory.Exists(path))
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles().OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
                Add(file.Name, file.Length, file.OpenRead);
        }
        else
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(entry => entry.Name.Length > 0)
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
                Add(entry.Name, entry.Length, entry.Open);
        }

        foreach (var group in files.GroupBy(file => file.IsDrill ? DrillKind(file.Role) : file.Role.Split(',')[0] is "Profile" ? "Profile" : file.Role))
            if (group.Count() > 1)
                throw Bad($"같은 역할의 제조 파일이 두 개 이상입니다. {group.Key}");

        var profiles = files.Where(file => file.Role.StartsWith("Profile", StringComparison.Ordinal)).ToArray();
        if (profiles.Length != 1)
            throw Bad("패널 또는 보드의 외곽 Gerber가 필요합니다.");
        var isPanel = files.Any(file => !file.IsDrill && file.Text.Contains("%TF.Part,Array*%", StringComparison.Ordinal));
        var outline = Geometry(profiles[0]);
        var bounds = isPanel ? RectangleBounds(outline) : PointBounds(OutlineContours(outline).SelectMany(path => path).ToArray());
        ValidateStack(files);
        return new CamInput(Path.GetFullPath(path), files, bounds, isPanel);
    }

    public static bool ValidRole(string role, bool drill) => Regex.IsMatch(role, drill
        ? @"^(?:Plated,[1-9]\d?,[1-9]\d?,PTH|NonPlated,[1-9]\d?,[1-9]\d?,NPTH)$"
        : @"^(?:Copper,L[1-9]\d?,(?:Top|Bot|Inr)|(?:Soldermask|Paste|Legend),(?:Top|Bot)|Profile(?:,(?:P|NP))?|Vcut)$");

    internal static string GerberAttribute(string source, string attribute)
    {
        var extended = Regex.Match(source, $@"%TF\.{attribute},([^*\r\n]+)\*%");
        return extended.Success ? extended.Groups[1].Value
            : Regex.Match(source, $@"(?m)^[ \t]*G04[ \t]+#@![ \t]+TF\.{attribute},([^*\r\n]+)\*").Groups[1].Value;
    }

    public static CamPanel Build(CamInput input, PanelOptions options)
    {
        if (input.IsPanel)
            throw Bad("단일 보드 Gerber·드릴 세트를 지정하세요.");
        if (options.Rows < 1 || options.Columns < 1 || (long)options.Rows * options.Columns > MaxCopies)
            throw Bad($"배열은 1개부터 {MaxCopies}개까지 지원합니다.");
        if (new[] { options.RailLeft, options.RailRight, options.RailBottom, options.RailTop, options.CutClearance }
            .Any(value => value is < 0 or > 10000))
            throw Bad("레일 폭과 절단 이격은 0~10000 mm 범위여야 합니다.");
        if (options.Fiducials && (options.FiducialDiameter <= 0 || options.MaskDiameter <= options.FiducialDiameter))
            throw Bad("피두셜 동박 지름과 그보다 큰 마스크 개구 지름을 지정하세요.");
        if (options.Fiducials && !input.Files.Any(file => file.Role == "Soldermask,Top"))
            throw Bad("피두셜을 지정하려면 상면 솔더마스크가 필요합니다.");
        if (options.Tooling && options.ToolingDiameter <= 0)
            throw Bad("고정용 홀 지름을 입력하세요.");
        if (input.Files.Any(file => file.Role == "Vcut"))
            throw Bad("이미 V-cut 레이어를 가진 제조 세트입니다.");
        if (input.Files.Any(file => file.Role.StartsWith("Profile,P", StringComparison.Ordinal)))
            throw Bad("도금된 외곽은 지원 대상에서 제외됩니다.");

        var source = RectangleBounds(Geometry(input.Files.Single(file => file.Role.StartsWith("Profile", StringComparison.Ordinal))));
        var boards = source with
        {
            MaxX = source.MinX + options.Columns * source.Width,
            MaxY = source.MinY + options.Rows * source.Height
        };
        var panel = new CamBounds(boards.MinX - options.RailLeft, boards.MinY - options.RailBottom,
            boards.MaxX + options.RailRight, boards.MaxY + options.RailTop);
        if (panel.Width > 10000 || panel.Height > 10000)
            throw Bad("패널 치수가 지원 범위를 초과했습니다.");
        var copyCount = options.Rows * options.Columns;
        if (input.Files.Sum(file => (long)file.Text.Length) * copyCount > MaxGeneratedBytes)
            throw Bad("배열을 줄이세요. 생성할 제조 파일의 크기가 지원 범위를 초과했습니다.");

        var cuts = Cuts(source, boards, panel, options);
        var hasLeftCut = options.Columns > 1 || options.RailLeft > 0;
        var hasRightCut = options.Columns > 1 || options.RailRight > 0;
        var hasBottomCut = options.Rows > 1 || options.RailBottom > 0;
        var hasTopCut = options.Rows > 1 || options.RailTop > 0;
        var shapes = new Dictionary<CamFile, IReadOnlyList<CamShape>>();
        foreach (var file in input.Files)
        {
            if (file.Role.StartsWith("Profile", StringComparison.Ordinal))
                continue;
            var geometry = Geometry(file);
            shapes[file] = geometry;
            var conductor = file.IsDrill || file.Role.StartsWith("Copper,", StringComparison.Ordinal);
            foreach (var shape in geometry.Where(shape => conductor || !shape.Dark))
            {
                var extent = ShapeBounds(shape);
                if (extent.MinX < source.MinX || extent.MinY < source.MinY ||
                    extent.MaxX > source.MaxX || extent.MaxY > source.MaxY)
                    throw Bad($"{file.Name}: 보드 외곽을 벗어나는 동박, 홀 또는 지움 형상이 있습니다.");
                if (conductor && shape.Dark && ((hasLeftCut && extent.MinX - source.MinX < options.CutClearance) ||
                    (hasRightCut && source.MaxX - extent.MaxX < options.CutClearance) ||
                    (hasBottomCut && extent.MinY - source.MinY < options.CutClearance) ||
                    (hasTopCut && source.MaxY - extent.MaxY < options.CutClearance)))
                    throw Bad($"{file.Name}: 동박 또는 홀과 V-cut 사이에 {Format(options.CutClearance)} mm가 필요합니다. 위치 {Format(shape.Points[0].X)}, {Format(shape.Points[0].Y)} mm.");
            }
        }

        var marks = new List<(CamPoint Point, decimal Radius)>();
        var fiducials = options.Fiducials ? Marks(panel, boards, cuts, options.CutClearance,
            options.MaskDiameter / 2, options.FiducialPositions, marks) : [];
        var tooling = options.Tooling ? Marks(panel, boards, cuts, options.CutClearance,
            options.ToolingDiameter / 2, options.ToolingPositions, marks) : [];
        var files = new List<CamFile>();
        foreach (var (file, geometry) in shapes)
        {
            var text = file.IsDrill
                ? RepeatDrill(file, source, options, DrillKind(file.Role) == "NPTH" ? tooling : [])
                : RepeatGerber(file, geometry.Count > 0, source, options, fiducials);
            files.Add(file with { Text = text });
        }
        var layerCount = files.Count(file => file.Role.StartsWith("Copper,", StringComparison.Ordinal));
        if (tooling.Count > 0 && !files.Any(file => file.IsDrill && DrillKind(file.Role) == "NPTH"))
        {
            var role = $"NonPlated,1,{layerCount},NPTH";
            files.Add(new CamFile("panel-NPTH.drl", role, RepeatDrill(new("panel-NPTH.drl", role,
                $"M48\n; #@! TF.FileFunction,{role}\nMETRIC\n%\nG90\nG05\nM30\n", true),
                source, options, tooling), true));
        }
        var bottomLeft = new CamPoint(panel.MinX, panel.MinY);
        var bottomRight = new CamPoint(panel.MaxX, panel.MinY);
        var topRight = new CamPoint(panel.MaxX, panel.MaxY);
        var topLeft = new CamPoint(panel.MinX, panel.MaxY);
        files.Add(LineFile("panel-Profile.gbr", "Profile,NP", [new(bottomLeft, bottomRight),
            new(bottomRight, topRight), new(topRight, topLeft), new(topLeft, bottomLeft)]));
        if (cuts.Count > 0)
            files.Add(LineFile("panel-Vcut.gbr", "Vcut", cuts));
        var warnings = input.Files.Any(file => file.IsDrill)
            ? Array.Empty<string>()
            : ["원본 드릴 파일이 없습니다. 단품에 가공 홀이 없는지 확인하세요."];
        return new CamPanel(files, panel, cuts, fiducials, tooling, warnings);
    }

    public static void Export(CamPanel panel, string path)
    {
        path = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in panel.Files)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(file.Name, CompressionLevel.Optimal).Open(), Utf8);
                    writer.Write(file.Text);
                }
            }
            var result = Load(temporary);
            if (result.Bounds != panel.Bounds || result.Files.Count != panel.Files.Count)
                throw Bad("저장한 패널의 외곽이나 파일 수가 일치하지 않습니다.");
            foreach (var file in result.Files)
                _ = Geometry(file);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ValidateStack(IReadOnlyList<CamFile> files)
    {
        var copper = files.Where(file => file.Role.StartsWith("Copper,", StringComparison.Ordinal)).
            Select(file => file.Role.Split(',')).OrderBy(parts => int.Parse(parts[1][1..], Invariant)).ToArray();
        if (copper.Length < 2 || copper[0][1] != "L1" || copper[0][2] != "Top" || copper[^1][2] != "Bot")
            throw Bad("상면과 하면 동박을 포함한 Gerber 스택이 필요합니다.");
        for (var index = 1; index < copper.Length; index++)
            if (copper[index][1] != $"L{index + 1}" || (index < copper.Length - 1 && copper[index][2] != "Inr"))
                throw Bad("동박 스택 번호가 연속적이지 않거나 내부층 역할이 잘못되었습니다.");
        foreach (var file in files.Where(file => file.IsDrill))
        {
            var fields = file.Role.Split(',');
            if (fields[1] != "1" || fields[2] != copper.Length.ToString(Invariant))
                throw Bad($"{file.Name}: 드릴 레이어 범위가 전체 동박층과 일치하지 않습니다.");
        }
    }

    private static CamBounds RectangleBounds(IReadOnlyList<CamShape> geometry)
    {
        if (geometry.Count != 4 || geometry.Any(shape => shape.Kind != "Line" || !shape.Dark))
            throw Bad("V-cut 패널 생성에는 네 개의 직선으로 닫힌 직사각형 외곽이 필요합니다.");
        var points = geometry.SelectMany(shape => shape.Points).ToArray();
        var bounds = PointBounds(points);
        var corners = new HashSet<CamPoint> { new(bounds.MinX, bounds.MinY), new(bounds.MinX, bounds.MaxY),
            new(bounds.MaxX, bounds.MinY), new(bounds.MaxX, bounds.MaxY) };
        if (bounds.Width <= 0 || bounds.Height <= 0 || points.GroupBy(point => point).Any(group => group.Count() != 2)
            || !corners.SetEquals(points)
            || geometry.Any(shape => (shape.Points[0].X == shape.Points[1].X) == (shape.Points[0].Y == shape.Points[1].Y)))
            throw Bad("외곽선이 축에 평행한 직사각형을 이루지 않습니다.");
        var edges = geometry.Select(shape =>
            (shape.Points[0].X == shape.Points[1].X, Math.Min(shape.Points[0].X, shape.Points[1].X),
                Math.Min(shape.Points[0].Y, shape.Points[1].Y))).Distinct().Count();
        if (edges != 4)
            throw Bad("외곽선에 중복 선이 있습니다.");
        return bounds;
    }

    internal static IReadOnlyList<IReadOnlyList<CamPoint>> OutlineContours(IReadOnlyList<CamShape> outline)
    {
        if (outline.Count == 0 || outline.Any(shape => shape.Kind is not ("Line" or "Arc") || !shape.Dark || shape.Points.Count < 2))
            throw Bad("외곽 Gerber에는 닫힌 직선 또는 원호 경계가 필요합니다.");

        // 플로터가 선분을 임의 순서와 방향으로 기록해도 끝점으로 외곽을 연결한다.
        var atEndpoint = outline.SelectMany((shape, index) => new[] { (Point: shape.Points[0], Index: index),
            (Point: shape.Points[^1], Index: index) }).ToLookup(endpoint => endpoint.Point, endpoint => endpoint.Index);
        if (atEndpoint.Any(endpoint => endpoint.Count() != 2))
            throw Bad("외곽 Gerber에 끊어졌거나 겹치는 경계가 있습니다.");

        var used = new HashSet<int>();
        var contours = new List<IReadOnlyList<CamPoint>>();
        for (var index = 0; index < outline.Count; index++)
        {
            if (!used.Add(index))
                continue;
            var contour = outline[index].Points.ToList();
            while (contour[0] != contour[^1])
            {
                var next = atEndpoint[contour[^1]].FirstOrDefault(edge => !used.Contains(edge), -1);
                if (next == -1)
                    throw Bad("외곽 Gerber의 경계를 닫을 수 없습니다.");
                used.Add(next);
                var points = outline[next].Points;
                contour.AddRange((points[0] == contour[^1] ? points : points.Reverse()).Skip(1));
            }
            if (contour.Take(contour.Count - 1).Distinct().Count() < 3)
                throw Bad("외곽 Gerber의 닫힌 경계가 면을 이루지 않습니다.");
            contours.Add(contour);
        }
        return contours;
    }

    private static List<CamLine> Cuts(CamBounds source, CamBounds boards, CamBounds panel, PanelOptions options)
    {
        var cuts = new List<CamLine>();
        for (var index = 0; index <= options.Columns; index++)
        {
            if ((index == 0 && boards.MinX == panel.MinX) ||
                (index == options.Columns && boards.MaxX == panel.MaxX))
                continue;
            var x = source.MinX + index * source.Width;
            cuts.Add(new CamLine(new(x, panel.MinY), new(x, panel.MaxY)));
        }
        for (var index = 0; index <= options.Rows; index++)
        {
            if ((index == 0 && boards.MinY == panel.MinY) ||
                (index == options.Rows && boards.MaxY == panel.MaxY))
                continue;
            var y = source.MinY + index * source.Height;
            cuts.Add(new CamLine(new(panel.MinX, y), new(panel.MaxX, y)));
        }
        return cuts;
    }

    private static List<CamPoint> Marks(CamBounds panel, CamBounds boards, IReadOnlyList<CamLine> cuts,
        decimal clearance, decimal radius, IReadOnlyList<CamPoint>? manual, List<(CamPoint Point, decimal Radius)> occupied)
    {
        bool Fits(CamPoint point)
        {
            var gap = clearance + radius;
            if (point.X - panel.MinX < gap || panel.MaxX - point.X < gap ||
                point.Y - panel.MinY < gap || panel.MaxY - point.Y < gap ||
                (point.X > boards.MinX - gap && point.X < boards.MaxX + gap &&
                    point.Y > boards.MinY - gap && point.Y < boards.MaxY + gap))
                return false;
            if (cuts.Any(cut => cut.From.X == cut.To.X ? Math.Abs(point.X - cut.From.X) < gap
                : Math.Abs(point.Y - cut.From.Y) < gap))
                return false;
            return occupied.All(mark => Square(point.X - mark.Point.X) + Square(point.Y - mark.Point.Y)
                >= Square(radius + mark.Radius + clearance));
        }

        var marks = new List<CamPoint>();
        if (manual is { Count: > 0 })
        {
            if (manual.Count != 3)
                throw Bad("레일에 서로 다른 위치 세 개를 지정하세요.");
            foreach (var point in manual)
            {
                var snapped = Snap(point);
                if (!Fits(snapped))
                    throw Bad($"레일 마크 위치가 외곽, 절단선 또는 다른 마크와 충돌합니다. {snapped.X}, {snapped.Y} mm.");
                occupied.Add((snapped, radius));
                marks.Add(snapped);
            }
            if (!NonCollinear(marks[0], marks[1], marks[2]))
                throw Bad("세 레일 마크를 한 직선 위에 둘 수 없습니다.");
            return marks;
        }

        var xs = cuts.Where(cut => cut.From.X == cut.To.X).Select(cut => cut.From.X)
            .Append(panel.MinX).Append(panel.MaxX).Distinct().Order().ToArray();
        var ys = cuts.Where(cut => cut.From.Y == cut.To.Y).Select(cut => cut.From.Y)
            .Append(panel.MinY).Append(panel.MaxY).Distinct().Order().ToArray();
        var candidates = new List<CamPoint>();
        for (var x = 1; x < xs.Length; x++)
            for (var y = 1; y < ys.Length; y++)
                foreach (var fraction in new[] { .25m, .5m, .75m })
                {
                    candidates.Add(Snap(new(xs[x - 1] + (xs[x] - xs[x - 1]) * fraction,
                        (ys[y - 1] + ys[y]) / 2)));
                    candidates.Add(Snap(new((xs[x - 1] + xs[x]) / 2,
                        ys[y - 1] + (ys[y] - ys[y - 1]) * fraction)));
                }
        foreach (var corner in new CamPoint[] { new(panel.MinX, panel.MinY), new(panel.MaxX, panel.MaxY), new(panel.MaxX, panel.MinY) })
        {
            var selected = candidates.Where(point => Fits(point) && (marks.Count != 2 || NonCollinear(marks[0], marks[1], point)))
                .OrderBy(point => Square(point.X - corner.X) + Square(point.Y - corner.Y))
                .Select(point => (CamPoint?)point).FirstOrDefault();
            if (selected is null)
                throw Bad("레일에 마크 세 개를 배치할 공간이 없습니다. 레일 폭 또는 마크 치수를 확인하세요.");
            occupied.Add((selected.Value, radius));
            marks.Add(selected.Value);
        }
        return marks;
    }

    private static CamPoint Snap(CamPoint point) => new(decimal.Round(point.X, 5), decimal.Round(point.Y, 5));
    private static decimal Square(decimal value) => value * value;
    private static bool NonCollinear(CamPoint a, CamPoint b, CamPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) != (b.Y - a.Y) * (c.X - a.X);

    private static CamBounds PointBounds(IReadOnlyList<CamPoint> points) => new(points.Min(p => p.X),
        points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));

    internal static CamBounds ShapeBounds(CamShape shape)
    {
        if (shape.Contours is { Count: > 0 })
            return PointBounds(shape.Contours.SelectMany(contour => contour).ToArray());
        var points = PointBounds(shape.Points);
        var arcPadding = shape.Kind == "Arc" ? ArcTolerance : 0;
        return new CamBounds(points.MinX - shape.Width / 2 - arcPadding, points.MinY - shape.Height / 2 - arcPadding,
            points.MaxX + shape.Width / 2 + arcPadding, points.MaxY + shape.Height / 2 + arcPadding);
    }

    public static IReadOnlyList<CamShape> Geometry(CamFile file)
    {
        try
        {
            return file.IsDrill ? DrillGeometry(file.Text) : GerberGeometry(file.Text);
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"{file.Name}: {error.Message}", error);
        }
    }

    private static IEnumerable<string> Commands(string source)
    {
        var index = 0;
        foreach (Match match in Command.Matches(source))
        {
            index = match.Index + match.Length;
            yield return match.Groups[1].Value.Trim();
        }
        if (source.AsSpan(index).Trim().Length != 0)
            throw Bad("완전하지 않은 Gerber 명령이 있습니다.");
    }

    private sealed record ApertureShape(string Name, decimal Width, decimal Height, bool Fiducial,
        IReadOnlyList<CamPoint>? Outline = null, decimal Hole = 0);

    private static List<CamShape> GerberGeometry(string source)
    {
        var shapes = new List<CamShape>();
        var apertures = new Dictionary<int, ApertureShape>();
        var macros = new Dictionary<string, string>();
        (int X, int Y)? coordinateFormat = null;
        var metric = false;
        var end = false;
        bool dark = true, fiduciary = false, multiturn = false;
        var interpolation = "G01";
        CamPoint? current = null;
        ApertureShape? selected = null;
        List<List<CamPoint>>? region = null;
        (int Begin, int X, int Y, decimal Dx, decimal Dy)? repeat = null;

        foreach (var command in Commands(source))
        {
            if (end)
                throw Bad("M02 뒤에 Gerber 명령이 있습니다.");
            if (command == "M02*")
            {
                end = true;
                continue;
            }
            if (command.StartsWith("G04", StringComparison.Ordinal) || command.StartsWith("%TF.", StringComparison.Ordinal)
                || command.StartsWith("%TO.", StringComparison.Ordinal))
                continue;
            if (command.StartsWith("%TA.", StringComparison.Ordinal))
            {
                if (command.StartsWith("%TA.AperFunction,", StringComparison.Ordinal))
                    fiduciary = command.StartsWith("%TA.AperFunction,FiducialPad,", StringComparison.Ordinal);
                continue;
            }
            if (command.StartsWith("%TD", StringComparison.Ordinal))
            {
                if (command is "%TD*%" or "%TD.AperFunction*%")
                    fiduciary = false;
                continue;
            }
            if (command == "%MOMM*%")
            {
                metric = true;
                continue;
            }
            if (GerberFormat.Match(command) is { Success: true } format)
            {
                if (coordinateFormat.HasValue || shapes.Count > 0)
                    throw Bad("좌표 형식이 중복되었습니다.");
                coordinateFormat = (int.Parse(format.Groups[2].Value, Invariant), int.Parse(format.Groups[4].Value, Invariant));
                continue;
            }
            if (command is "%LPD*%" or "%LPC*%")
            {
                if (region is not null)
                    throw Bad("닫히지 않은 리전에서 그리기 극성을 변경했습니다.");
                dark = command == "%LPD*%";
                continue;
            }
            if (command.StartsWith("%AM", StringComparison.Ordinal))
            {
                var nameEnd = command.IndexOf('*');
                if (nameEnd <= 3 || !macros.TryAdd(command[3..nameEnd], command[(nameEnd + 1)..^1]))
                    throw Bad("아퍼처 매크로가 잘못되었거나 중복되었습니다.");
                continue;
            }
            if (Aperture.Match(command) is { Success: true } aperture)
            {
                var code = int.Parse(aperture.Groups[1].Value, Invariant);
                if (code < 10 || !apertures.TryAdd(code, ParseAperture(aperture.Groups[2].Value,
                    aperture.Groups[3].Value, fiduciary, macros)))
                    throw Bad("아퍼처 번호가 잘못되었거나 중복되었습니다.");
                continue;
            }
            if (command.Length > 2 && command[0] == 'D' && int.TryParse(command.AsSpan(1, command.Length - 2), out var d) && d >= 10)
            {
                if (!apertures.TryGetValue(d, out selected))
                    throw Bad($"정의되지 않은 아퍼처 D{d}입니다.");
                continue;
            }
            if (command is "G01*" or "G02*" or "G03*")
            {
                interpolation = command[..3];
                continue;
            }
            if (command == "G75*")
            {
                multiturn = true;
                continue;
            }
            if (command == "G36*" && region is null)
            {
                region = [];
                continue;
            }
            if (command == "G37*" && region is not null)
            {
                if (region.Count == 0 || region.Any(contour => contour.Count < 4 || contour[0] != contour[^1]))
                    throw Bad("리전 윤곽이 닫히지 않았습니다.");
                shapes.Add(new CamShape("Region", region[0], 0, 0, dark, Contours: region));
                region = null;
                continue;
            }
            if (Repeat.Match(command) is { Success: true } step)
            {
                if (repeat is not null || region is not null)
                    throw Bad("중첩되거나 리전 내부인 Step & Repeat를 지원하지 않습니다.");
                var xCount = int.Parse(step.Groups[1].Value, Invariant);
                var yCount = int.Parse(step.Groups[2].Value, Invariant);
                if (xCount < 1 || yCount < 1 || (long)xCount * yCount > MaxCopies)
                    throw Bad("Step & Repeat 복제 개수가 지원 범위를 초과했습니다.");
                repeat = (shapes.Count, xCount, yCount, Number(step.Groups[3].Value), Number(step.Groups[4].Value));
                continue;
            }
            if (command == "%SR*%" && repeat is { } block && region is null)
            {
                var originals = shapes.GetRange(block.Begin, shapes.Count - block.Begin);
                for (var column = 0; column < block.X; column++)
                    for (var row = 0; row < block.Y; row++)
                    {
                        if (column == 0 && row == 0)
                            continue;
                        shapes.AddRange(originals.Select(shape => Translate(shape, column * block.Dx, row * block.Dy)));
                    }
                repeat = null;
                current = null;
                continue;
            }

            var operation = Operation.Match(command);
            if (!metric || !coordinateFormat.HasValue || !operation.Success)
                throw Bad($"지원하지 않는 Gerber 명령입니다. {command}");
            var p = coordinateFormat.Value;
            var groups = operation.Groups;
            if (groups[1].Success)
                interpolation = groups[1].Value;
            var point = new CamPoint(
                groups[2].Success ? Number(groups[2].Value) / Scale(p.X) : current?.X ?? throw Bad("첫 X 좌표가 없습니다."),
                groups[3].Success ? Number(groups[3].Value) / Scale(p.Y) : current?.Y ?? throw Bad("첫 Y 좌표가 없습니다."));
            if (groups[6].Value == "2")
            {
                if (region is not null)
                    region.Add([point]);
            }
            else if (groups[6].Value == "3" && selected is { } flash && region is null)
            {
                List<IReadOnlyList<CamPoint>>? contours = null;
                if (flash.Outline is { } outline)
                    contours = [outline.Select(vertex => new CamPoint(vertex.X + point.X, vertex.Y + point.Y)).ToArray()];
                if (flash.Hole > 0)
                {
                    contours ??= [
                        flash.Name == "C" ? Disk(point, flash.Width / 2) :
                        Box(point.X - flash.Width / 2, point.Y - flash.Height / 2,
                            point.X + flash.Width / 2, point.Y + flash.Height / 2)
                    ];
                    contours.Add(Disk(point, flash.Hole / 2));
                }
                shapes.Add(new CamShape("Flash", [point], flash.Width, flash.Height, dark,
                    flash.Name == "C", flash.Fiducial, contours));
            }
            else if (groups[6].Value == "1" && current is { } start)
            {
                IReadOnlyList<CamPoint> line;
                var arc = interpolation != "G01";
                if (arc)
                {
                    if (!multiturn || (!groups[4].Success && !groups[5].Success))
                        throw Bad("G75 원호의 중심 오프셋을 찾을 수 없습니다.");
                    var center = new CamPoint(start.X + (groups[4].Success ? Number(groups[4].Value) / Scale(p.X) : 0),
                        start.Y + (groups[5].Success ? Number(groups[5].Value) / Scale(p.Y) : 0));
                    line = Arc(start, point, center, interpolation == "G02");
                }
                else
                    line = [start, point];
                if (region is { Count: > 0 })
                    region[^1].AddRange(line.Skip(1));
                else if (region is null && selected is { Name: "C", Hole: 0 } pen)
                    shapes.Add(new CamShape(arc ? "Arc" : "Line", line, pen.Width, pen.Height, dark, true, pen.Fiducial));
                else
                    throw Bad("스트로크에는 구멍 없는 원형 아퍼처가 필요합니다.");
            }
            else
                throw Bad($"Gerber 작업의 아퍼처 또는 시작 좌표가 없습니다. {command}");
            current = point;
        }
        if (!metric || !coordinateFormat.HasValue || !end || region is not null || repeat is not null)
            throw Bad("mm 좌표 형식, 종료 명령 또는 닫힌 블록을 확인할 수 없습니다.");
        return shapes;
    }

    private static IReadOnlyList<CamPoint> Arc(CamPoint start, CamPoint end, CamPoint center, bool clockwise)
    {
        var startRadius = Math.Sqrt((double)(Square(start.X - center.X) + Square(start.Y - center.Y)));
        var endRadius = Math.Sqrt((double)(Square(end.X - center.X) + Square(end.Y - center.Y)));
        if (startRadius <= 0 || Math.Abs(startRadius - endRadius) > .002)
            throw Bad("원호 반지름이 일치하지 않습니다.");
        var from = Math.Atan2((double)(start.Y - center.Y), (double)(start.X - center.X));
        var to = Math.Atan2((double)(end.Y - center.Y), (double)(end.X - center.X));
        var sweep = to - from;
        if (clockwise && sweep >= 0) sweep -= 2 * Math.PI;
        if (!clockwise && sweep <= 0) sweep += 2 * Math.PI;
        var maxAngle = 2 * Math.Acos(Math.Max(-1, 1 - (double)ArcTolerance / startRadius));
        var count = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweep) / maxAngle));
        if (count > 10_000)
            throw Bad("원호 반지름이 렌더링 지원 범위를 초과했습니다.");
        var points = new List<CamPoint>(count + 1) { start };
        for (var index = 1; index < count; index++)
        {
            var fraction = index / (double)count;
            var angle = from + sweep * fraction;
            var radius = startRadius + (endRadius - startRadius) * fraction;
            points.Add(new CamPoint(center.X + (decimal)(radius * Math.Cos(angle)),
                center.Y + (decimal)(radius * Math.Sin(angle))));
        }
        points.Add(end);
        return points;
    }

    private static ApertureShape ParseAperture(string name, string modifiers, bool fiducial, Dictionary<string, string> macros)
    {
        var values = modifiers.Length == 0 ? [] : modifiers.Split('X').Select(Number).ToArray();
        if (name == "C" && values.Length is 1 or 2 && values[0] >= 0)
            return new ApertureShape(name, values[0], values[0], fiducial, Hole: values.Length == 2 ? values[1] : 0);
        if ((name is "R" or "O") && values.Length is 2 or 3 && values[0] > 0 && values[1] > 0)
        {
            IReadOnlyList<CamPoint>? outline = null;
            if (name == "O")
            {
                var radius = Math.Min(values[0], values[1]) / 2;
                var half = Math.Abs(values[0] - values[1]) / 2;
                outline = values[0] >= values[1]
                    ? Hull(Disk(new(-half, 0), radius).Concat(Disk(new(half, 0), radius)))
                    : Hull(Disk(new(0, -half), radius).Concat(Disk(new(0, half), radius)));
            }
            return new ApertureShape(name, values[0], values[1], fiducial, outline, values.Length == 3 ? values[2] : 0);
        }
        if (name == "P" && values.Length is >= 2 and <= 4 && values[0] > 0 &&
            values[1] is >= 3 and <= 12 && decimal.Truncate(values[1]) == values[1])
        {
            var outline = Disk(new(0, 0), values[0] / 2, (int)values[1], values.Length > 2 ? values[2] : 0);
            return new ApertureShape(name, values[0], values[0], fiducial, outline, values.Length == 4 ? values[3] : 0);
        }
        if (name == "RoundRect" && values.Length is 9 or 10 && values[0] > 0 && macros.TryGetValue(name, out var macro))
        {
            var primitives = macro.Split('*', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.StartsWith("0 ", StringComparison.Ordinal)).Select(part => part.Replace(" ", "")).ToArray();
            string[] expected = ["4,1,4,$2,$3,$4,$5,$6,$7,$8,$9,$2,$3,0", "1,1,$1+$1,$2,$3",
                "1,1,$1+$1,$4,$5", "1,1,$1+$1,$6,$7", "1,1,$1+$1,$8,$9", "20,1,$1+$1,$2,$3,$4,$5,0",
                "20,1,$1+$1,$4,$5,$6,$7,0", "20,1,$1+$1,$6,$7,$8,$9,0", "20,1,$1+$1,$8,$9,$2,$3,0"];
            if (primitives.SequenceEqual(expected))
            {
                var centers = Enumerable.Range(0, 4).Select(index => new CamPoint(values[1 + index * 2], values[2 + index * 2]));
                var outline = Hull(centers.SelectMany(center => Disk(center, values[0])));
                var bounds = PointBounds(outline);
                return new ApertureShape(name, bounds.Width, bounds.Height, fiducial, outline);
            }
        }
        throw Bad($"지원하지 않는 아퍼처 형상입니다. {name}");
    }

    private static IReadOnlyList<CamPoint> Disk(CamPoint center, decimal radius, int count = 40, decimal angle = 0)
    {
        var vertices = new List<CamPoint>(count + 1);
        for (var index = 0; index < count; index++)
        {
            var radians = (double)angle * Math.PI / 180 + 2 * Math.PI * index / count;
            vertices.Add(new CamPoint(center.X + (decimal)Math.Cos(radians) * radius, center.Y + (decimal)Math.Sin(radians) * radius));
        }
        vertices.Add(vertices[0]);
        return vertices;
    }

    private static IReadOnlyList<CamPoint> Box(decimal minX, decimal minY, decimal maxX, decimal maxY) =>
        [new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY), new(minX, minY)];

    private static IReadOnlyList<CamPoint> Hull(IEnumerable<CamPoint> points)
    {
        decimal Turn(CamPoint a, CamPoint b, CamPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var ordered = points.Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        var hull = new List<CamPoint>();
        foreach (var point in ordered)
        {
            while (hull.Count >= 2 && Turn(hull[^2], hull[^1], point) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        var lower = hull.Count;
        foreach (var point in ordered.Reverse().Skip(1))
        {
            while (hull.Count > lower && Turn(hull[^2], hull[^1], point) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        return hull;
    }

    public static CamShape Translate(CamShape shape, decimal x, decimal y)
    {
        IReadOnlyList<CamPoint> Move(IReadOnlyList<CamPoint> points) =>
            points.Select(point => new CamPoint(point.X + x, point.Y + y)).ToArray();
        return shape with { Points = Move(shape.Points), Contours = shape.Contours?.Select(Move).ToArray() };
    }

    private static decimal Scale(int precision) => (decimal)Math.Pow(10, precision);

    private sealed record DrillData(string[] Header, IReadOnlyDictionary<int, decimal> Tools,
        IReadOnlyList<(int Id, CamPoint Point)> Hits);

    private static DrillData ReadDrill(string source)
    {
        var lines = source.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var tools = new Dictionary<int, decimal>();
        var hits = new List<(int, CamPoint)>();
        if (lines.Length < 4 || lines[0] != "M48" || lines[^1] != "M30")
            throw Bad("Excellon 헤더와 M30 종료가 필요합니다.");
        var headerEnd = Array.IndexOf(lines, "%");
        var formatInHeader = headerEnd > 0 && lines.Take(headerEnd).Any(line =>
            line.StartsWith("; FORMAT=", StringComparison.Ordinal) &&
            line.Contains("absolute / metric / decimal}", StringComparison.OrdinalIgnoreCase));
        if (headerEnd < 0 || !lines.Take(headerEnd).Contains("METRIC") ||
            lines.Take(headerEnd).Any(line => line.StartsWith("; FORMAT=", StringComparison.Ordinal) &&
                !line.Contains("absolute / metric / decimal}", StringComparison.OrdinalIgnoreCase)))
            throw Bad("mm 단위 소수점 절대 좌표 Excellon 드릴 파일이 필요합니다.");
        foreach (var line in lines[1..headerEnd])
        {
            if (line is "METRIC" or "FMAT,2" || line.StartsWith(';'))
                continue;
            var match = Tool.Match(line);
            if (!match.Success || !tools.TryAdd(int.Parse(match.Groups[1].Value, Invariant), Number(match.Groups[2].Value)))
                throw Bad($"지원하지 않거나 중복된 드릴 도구 정의입니다. {line}");
        }
        if (tools.Values.Any(value => value <= 0))
            throw Bad("드릴 도구의 지름이 잘못되었습니다.");
        var tool = 0;
        foreach (var line in lines[(headerEnd + 1)..^1])
        {
            if (line is "G90" or "G05" || line.StartsWith(';'))
                continue;
            if (line[0] == 'T' && int.TryParse(line.AsSpan(1), out var index) && tools.ContainsKey(index))
            {
                tool = index;
                continue;
            }
            var hit = Hit.Match(line);
            if (!hit.Success || tool == 0)
                throw Bad($"지원하지 않는 원형 드릴 명령입니다. {line}");
            var x = hit.Groups[1].Value;
            var y = hit.Groups[2].Value;
            if (!x.Contains('.') && !y.Contains('.') && !formatInHeader)
                throw Bad("좌표 소수점 표기를 확인할 수 없습니다. KiCad에서 소수점 좌표와 전체 헤더로 내보내세요.");
            hits.Add((tool, new CamPoint(Number(x), Number(y))));
        }
        return new DrillData(lines[..headerEnd], tools, hits);
    }

    private static List<CamShape> DrillGeometry(string source)
    {
        var data = ReadDrill(source);
        return data.Hits.Select(hit => new CamShape("Drill", [hit.Point], data.Tools[hit.Id], data.Tools[hit.Id], true, true)).ToList();
    }

    private static string RepeatDrill(CamFile file, CamBounds bounds, PanelOptions options, IReadOnlyList<CamPoint> marks)
    {
        var data = ReadDrill(file.Text);
        var output = new StringBuilder();
        foreach (var line in data.Header)
            if (!line.StartsWith("; #@! TF.CreationDate,", StringComparison.Ordinal) &&
                !line.StartsWith("; #@! TF.GenerationSoftware,", StringComparison.Ordinal)
                && !line.StartsWith("; #@! TF.FileFunction,", StringComparison.Ordinal)
                && !line.StartsWith("; DRILL file ", StringComparison.Ordinal))
                output.AppendLine(line);

        // 수동 매핑이나 새 비도금 파일에도 가공 역할을 명시한다.
        output.Insert(output.ToString().IndexOf('\n') + 1,
            $"; #@! TF.GenerationSoftware,GerberHandler,GerberHandler{Environment.NewLine}; #@! TF.FileFunction,{file.Role}{Environment.NewLine}");
        var newTool = data.Tools.Keys.DefaultIfEmpty(0).Max() + 1;
        if (marks.Count > 0)
        {
            output.AppendLine("; #@! TA.AperFunction,NonPlated,NPTH,MechanicalDrill,Tooling");
            output.AppendLine($"T{newTool}C{DrillNumber(options.ToolingDiameter)}");
        }
        output.AppendLine("%").AppendLine("G90").AppendLine("G05");
        foreach (var group in data.Hits.GroupBy(hit => hit.Id))
        {
            output.AppendLine($"T{group.Key}");
            for (var column = 0; column < options.Columns; column++)
                for (var row = 0; row < options.Rows; row++)
                    foreach (var hit in group)
                        output.AppendLine(DrillPosition(new(hit.Point.X + column * bounds.Width, hit.Point.Y + row * bounds.Height)));
        }
        if (marks.Count > 0)
        {
            output.AppendLine($"T{newTool}");
            foreach (var mark in marks)
                output.AppendLine(DrillPosition(mark));
        }
        return output.AppendLine("M30").ToString();
    }

    private static string DrillNumber(decimal value) => value.ToString("0.0##########", Invariant);

    private static string DrillPosition(CamPoint point) => $"X{DrillNumber(point.X)}Y{DrillNumber(point.Y)}";

    private static string RepeatGerber(CamFile file, bool hasGeometry, CamBounds bounds, PanelOptions options, IReadOnlyList<CamPoint> marks)
    {
        var commands = Commands(file.Text).ToList();
        if (commands.Any(command => command.StartsWith("%SR", StringComparison.Ordinal)))
            throw Bad($"{file.Name}: 단일 PCB 입력에 기존 반복 블록이 포함되어 있습니다.");
        var output = new StringBuilder();
        output.AppendLine("%TF.GenerationSoftware,GerberHandler,GerberHandler*%");
        output.AppendLine("%TF.Part,Array*%");
        output.AppendLine($"%TF.FileFunction,{file.Role}*%");
        var polarity = GerberAttribute(file.Text, "FilePolarity");
        if (polarity is "Positive" or "Negative")
            output.AppendLine($"%TF.FilePolarity,{polarity}*%");
        commands.RemoveAll(command => command.StartsWith("%TF.Part,", StringComparison.Ordinal) ||
            command.StartsWith("%TF.ProjectId,", StringComparison.Ordinal) ||
            command.StartsWith("%TF.CreationDate,", StringComparison.Ordinal) ||
            command.StartsWith("%TF.GenerationSoftware,", StringComparison.Ordinal) ||
            command.StartsWith("%TF.FileFunction,", StringComparison.Ordinal) ||
            command.StartsWith("%TF.FilePolarity,", StringComparison.Ordinal) ||
            command.StartsWith("%TF.MD5,", StringComparison.Ordinal) ||
            Regex.IsMatch(command, @"^G04[ \t]+#@![ \t]+TF\.(Part|ProjectId|CreationDate|GenerationSoftware|FileFunction|FilePolarity|MD5),") ||
            command == "M02*");
        var format = commands.Select(command => GerberFormat.Match(command)).First(match => match.Success);
        var wholeX = int.Parse(format.Groups[1].Value, Invariant);
        var precisionX = int.Parse(format.Groups[2].Value, Invariant);
        var wholeY = int.Parse(format.Groups[3].Value, Invariant);
        var precisionY = int.Parse(format.Groups[4].Value, Invariant);
        string X(decimal value) => GerberPosition(value, wholeX, precisionX);
        string Y(decimal value) => GerberPosition(value, wholeY, precisionY);
        var first = commands.FindIndex(command => Operation.IsMatch(command) || command.StartsWith("%TO.", StringComparison.Ordinal)
            || (command.Length > 2 && command[0] == 'D' && char.IsDigit(command[1])) || command == "G36*");
        var split = first < 0 ? commands.Count : first;
        var body = commands.Skip(split).ToArray();
        var aperture = commands.Select(command => Aperture.Match(command)).Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, Invariant)).DefaultIfEmpty(9).Max() + 1;
        var topCopper = file.Role == "Copper,L1,Top";
        var topMask = file.Role == "Soldermask,Top";
        if (marks.Count > 0 && topMask && polarity != "Negative")
            throw Bad($"{file.Name}: 피두셜 개구를 추가하려면 음극성 상면 솔더마스크가 필요합니다.");

        foreach (var command in commands.Take(split))
            output.AppendLine(command);

        if (hasGeometry && options.Rows * options.Columns > 1)
        {
            // 넷·부품 속성이 있는 경우 보드별로 식별자를 바꿔 잘못된 전기적 연결을 방지한다.
            if (body.Any(command => command.StartsWith("%TO.", StringComparison.Ordinal)))
            {
                if (body.Any(command => command.StartsWith("%AD", StringComparison.Ordinal) ||
                    command.StartsWith("%AM", StringComparison.Ordinal)))
                    throw Bad($"{file.Name}: 객체 뒤에 아퍼처를 정의하는 파일은 배열할 수 없습니다.");
                var initialPolarity = commands.Take(split).LastOrDefault(command => command is "%LPD*%" or "%LPC*%") ?? "%LPD*%";
                var initialMode = commands.Take(split).LastOrDefault(command => command is "G01*" or "G02*" or "G03*") ?? "G01*";
                for (var column = 0; column < options.Columns; column++)
                    for (var row = 0; row < options.Rows; row++)
                    {
                        output.AppendLine("%TD*%").AppendLine(initialPolarity).AppendLine(initialMode);
                        var dx = column * bounds.Width;
                        var dy = row * bounds.Height;
                        foreach (var command in body)
                        {
                            if (Operation.IsMatch(command))
                                output.AppendLine(Coord.Replace(command, match =>
                                    match.Groups[1].Value == "X"
                                        ? "X" + X(Number(match.Groups[2].Value) / Scale(precisionX) + dx)
                                        : "Y" + Y(Number(match.Groups[2].Value) / Scale(precisionY) + dy)));
                            else
                                output.AppendLine(PrefixObject(command, $"R{row + 1}C{column + 1}_"));
                        }
                    }
            }
            else
            {
                X(bounds.MaxX + (options.Columns - 1) * bounds.Width);
                Y(bounds.MaxY + (options.Rows - 1) * bounds.Height);
                output.AppendLine($"%SRX{options.Columns}Y{options.Rows}I{Format(options.Columns > 1 ? bounds.Width : 0)}J{Format(options.Rows > 1 ? bounds.Height : 0)}*%");
                foreach (var command in body)
                    output.AppendLine(command);
                output.AppendLine("%SR*%");
            }
        }
        else
            foreach (var command in body)
                output.AppendLine(command);

        if (marks.Count > 0 && (topCopper || topMask))
        {
            output.AppendLine("%TD*%").AppendLine("%LPD*%").AppendLine("G01*");
            if (topCopper)
                output.AppendLine("%TA.AperFunction,FiducialPad,Panel*%");
            output.AppendLine($"%ADD{aperture}C,{Format(topCopper ? options.FiducialDiameter : options.MaskDiameter)}*%");
            output.AppendLine("%TD*%").AppendLine($"D{aperture}*");
            if (topCopper)
                output.AppendLine("%TO.N,*%");
            foreach (var mark in marks)
                output.AppendLine($"X{X(mark.X)}Y{Y(mark.Y)}D03*");
            output.AppendLine("%TD*%");
        }
        return output.AppendLine("M02*").ToString();
    }

    private static string PrefixObject(string command, string prefix)
    {
        if (!command.StartsWith("%TO.", StringComparison.Ordinal))
            return command;
        var parts = command[..^2].Split(',');
        if (parts[0] == "%TO.N")
        {
            for (var index = 1; index < parts.Length; index++)
                if (parts[index] is not ("" or "N/C"))
                    parts[index] = prefix + parts[index];
        }
        else if (parts.Length >= 2 && (parts[0] is "%TO.P" or "%TO.C"))
            parts[1] = prefix + parts[1];
        return string.Join(',', parts) + "*%";
    }

    private static string GerberPosition(decimal coordinate, int whole, int precision)
    {
        var scaled = coordinate * Scale(precision);
        if (decimal.Truncate(scaled) != scaled || Math.Abs(scaled) >= Scale(whole + precision))
            throw Bad("패널 좌표가 입력 Gerber 정밀도 또는 범위를 초과합니다.");
        return scaled.ToString("0", Invariant);
    }

    private static CamFile LineFile(string name, string role, IEnumerable<CamLine> lines)
    {
        var output = new StringBuilder();
        output.AppendLine("%TF.GenerationSoftware,GerberHandler,GerberHandler*%")
            .AppendLine("%TF.Part,Array*%").AppendLine($"%TF.FileFunction,{role}*%")
            .AppendLine("%FSLAX46Y46*%").AppendLine("%MOMM*%").AppendLine("%LPD*%")
            .AppendLine("%ADD10C,0.05*%").AppendLine("D10*").AppendLine("G01*");
        string Point(CamPoint point) => $"X{GerberPosition(point.X, 4, 6)}Y{GerberPosition(point.Y, 4, 6)}";
        foreach (var line in lines)
            output.AppendLine($"{Point(line.From)}D02*").AppendLine($"{Point(line.To)}D01*");
        return new CamFile(name, role, output.AppendLine("M02*").ToString(), false);
    }

    private static string DrillKind(string role) => role.StartsWith("NonPlated,", StringComparison.Ordinal) ? "NPTH" : "PTH";
    private static string Format(decimal value) => value.ToString("0.###########", Invariant);
    private static decimal Number(string value) => decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant);
    private static InvalidDataException Bad(string message) => new(message);
}
