using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using static GerberHandler.CamEngine;

namespace GerberHandler;

internal static class MixedCamFiles
{
    private static readonly Regex CommentAttribute = new(@"^G04[ \t]+#@![ \t]+(T[FAOD](?:\.[^*]+)?\*)$");
    private const string DrillAttribute = "; #@! TA.AperFunction,";
    private static readonly string[] TwoLayerCopper = ["Copper,L1,Top", "Copper,L2,Bot"];
    private static readonly string[] FourLayerCopper = ["Copper,L1,Top", "Copper,L2,Inr", "Copper,L3,Inr", "Copper,L4,Bot"];

    internal static IReadOnlyList<CamFile> Merge(IReadOnlyList<MixedBoard> boards, PanelOptions options,
        IReadOnlyList<CamPoint> fiducials, IReadOnlyList<CamPoint> tooling, IReadOnlyList<CamPoint> bites)
    {
        var stacks = boards.Select(board => Roles(board, "Copper,")).ToArray();
        var outerOnly = options.MapTwoLayerBoardsToOuterCopper && stacks.Any(stack => stack.SequenceEqual(TwoLayerCopper)) &&
            stacks.Any(stack => stack.SequenceEqual(FourLayerCopper)) &&
            stacks.All(stack => stack.SequenceEqual(TwoLayerCopper) || stack.SequenceEqual(FourLayerCopper));
        var copper = outerOnly ? FourLayerCopper : stacks[0];
        var masks = Roles(boards[0], "Soldermask,");
        for (var index = 0; index < boards.Count; index++)
        {
            var board = boards[index];
            if (!outerOnly && !stacks[index].SequenceEqual(copper))
                throw new InvalidDataException($"동박 스택 불일치 · {boards[0].Name} ({copper.Length}층) / " +
                    $"{board.Name} ({stacks[index].Length}층). " + (options.MapTwoLayerBoardsToOuterCopper
                        ? "외층 매핑은 2층·4층 조합을 지원합니다."
                        : "2층·4층 조합은 외층 매핑 옵션을 사용하세요."));
            if (!Roles(board, "Soldermask,").SequenceEqual(masks))
                throw new InvalidDataException("혼합 제조 ZIP의 모든 보드는 같은 면의 솔더마스크 레이어가 필요합니다.");
        }
        if (fiducials.Count > 0 && !masks.Contains("Soldermask,Top"))
            throw new InvalidDataException("피두셜을 만들려면 상면 솔더마스크가 필요합니다.");

        var files = new List<CamFile>();
        // 2층 보드는 외층 동박과 관통 드릴만 매핑한다. 내층 파일은 4층 보드에서만 가져온다.
        var sources = boards.SelectMany((board, index) => board.Input.Files.Select(file =>
                (File: outerOnly && stacks[index].Length == 2 ? MapOuterRole(file) : file, Board: board, Index: index + 1)))
            .Where(item => !item.File.Role.StartsWith("Profile", StringComparison.Ordinal) && item.File.Role != "Vcut");
        foreach (var layer in sources.Where(item => !item.File.IsDrill).GroupBy(item => item.File.Role))
            files.Add(MergeGerber(layer.Key, layer, options, fiducials));
        var drills = sources.Where(item => item.File.IsDrill).ToLookup(item => item.File.Role);
        foreach (var role in new[] { $"Plated,1,{copper.Length},PTH", $"NonPlated,1,{copper.Length},NPTH" })
        {
            var isNonPlated = role.StartsWith("NonPlated,", StringComparison.Ordinal);
            if (drills.Contains(role) || isNonPlated && (tooling.Count > 0 || bites.Count > 0))
                files.Add(MergeDrill(role, drills[role], isNonPlated ? tooling : [], isNonPlated ? bites : [], options));
        }
        return files;
    }

    private static CamFile MapOuterRole(CamFile file) => file.Role switch
    {
        "Copper,L2,Bot" => file with { Role = "Copper,L4,Bot" },
        "Plated,1,2,PTH" => file with { Role = "Plated,1,4,PTH" },
        "NonPlated,1,2,NPTH" => file with { Role = "NonPlated,1,4,NPTH" },
        _ => file
    };

    private static string[] Roles(MixedBoard board, string prefix) => board.Input.Files.Select(file => file.Role)
        .Where(role => role.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    private static CamFile MergeGerber(string role, IEnumerable<(CamFile File, MixedBoard Board, int Index)> items,
        PanelOptions options, IReadOnlyList<CamPoint> fiducials)
    {
        var sources = items.Select(item =>
        {
            _ = Geometry(item.File);
            var commands = Commands(item.File.Text).Select(ExpandCommentAttribute).ToArray();
            var format = commands.Select(command => GerberFormat.Match(command)).First(match => match.Success);
            var polarity = GerberAttribute(item.File.Text, "FilePolarity");
            if (polarity is not ("Positive" or "Negative"))
            {
                if (role.StartsWith("Soldermask,", StringComparison.Ordinal) || polarity.Length != 0)
                    throw Bad(item.File, "솔더마스크 또는 파일 극성을 확인할 수 없습니다.");
                polarity = "Positive";
            }
            return (item, commands, format, polarity);
        }).ToArray();
        var filePolarity = sources[0].polarity;
        if (sources.Any(source => source.polarity != filePolarity))
            throw new InvalidDataException($"{role}: 서로 다른 파일 극성의 레이어는 병합할 수 없습니다.");
        if (filePolarity == "Negative" && !role.StartsWith("Soldermask,", StringComparison.Ordinal))
            throw new InvalidDataException($"{role}: 음극성 레이어는 솔더마스크만 혼합할 수 있습니다.");

        var whole = Math.Max(4, sources.Max(s => Math.Max(GroupInt(s.format, 1), GroupInt(s.format, 3))));
        var precision = Math.Max(6, sources.Max(s => Math.Max(GroupInt(s.format, 2), GroupInt(s.format, 4))));
        string Position(decimal value) => GerberPosition(value, whole, precision);

        var definitions = new StringBuilder();
        var objects = new StringBuilder();
        var macros = new Dictionary<string, string>(StringComparer.Ordinal);
        var nextDCode = 10;
        // 파일별 플로터 상태를 초기화하고, 정의와 그리기를 각각 합친다.
        foreach (var (item, commands, format, _) in sources)
        {
            var apertureCodes = new Dictionary<int, int>();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var objectAttributes = new HashSet<string>(StringComparer.Ordinal);
            decimal? x = null, y = null;
            var sx = Scale(GroupInt(format, 2));
            var sy = Scale(GroupInt(format, 4));
            objects.AppendLine("%TD*%").AppendLine("%LPD*%").AppendLine("G01*");
            foreach (var command in commands)
            {
                if (command.StartsWith("%SR", StringComparison.Ordinal))
                    throw Bad(item.File, "이미 반복 블록이 들어 있는 Gerber는 혼합할 수 없습니다.");
                if (command.StartsWith("G04", StringComparison.Ordinal) || command.StartsWith("%TF.", StringComparison.Ordinal) ||
                    GerberFormat.IsMatch(command) || command is "M02*" or "%MOMM*%")
                    continue;
                if (command.StartsWith("%AM", StringComparison.Ordinal))
                {
                    var nameEnd = command.IndexOf('*');
                    var name = command[3..nameEnd];
                    var body = string.Concat(command[(nameEnd + 1)..^1].Split('*', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Where(part => !part.StartsWith("0 ", StringComparison.Ordinal)).Select(part => string.Concat(part.Where(c => !char.IsWhiteSpace(c))) + "*"));
                    if (macros.TryAdd(name, body))
                        definitions.AppendLine(command);
                    else if (macros[name] != body)
                        throw Bad(item.File, $"서로 다른 정의가 같은 아퍼처 매크로 이름을 사용합니다. {name}");
                    continue;
                }
                if (command.StartsWith("%TA.", StringComparison.Ordinal))
                {
                    attributes[AttributeName(command)] = command;
                    // 리전은 별도 아퍼처 없이 현재 TA 속성을 직접 사용한다.
                    objects.AppendLine(command);
                    continue;
                }
                if (command.StartsWith("%TD", StringComparison.Ordinal))
                {
                    if (command == "%TD*%")
                    {
                        attributes.Clear();
                        objectAttributes.Clear();
                        objects.AppendLine(command);
                    }
                    else
                    {
                        var name = AttributeName(command);
                        var apertureAttribute = attributes.Remove(name);
                        if (objectAttributes.Remove(name) || apertureAttribute) objects.AppendLine(command);
                    }
                    continue;
                }
                if (Aperture.Match(command) is { Success: true } aperture)
                {
                    var original = GroupInt(aperture, 1);
                    var assigned = nextDCode++;
                    apertureCodes.Add(original, assigned);
                    definitions.AppendLine("%TD*%");
                    foreach (var attribute in attributes.Values)
                        definitions.AppendLine(attribute);
                    definitions.AppendLine($"%ADD{assigned}{ApertureDefinition(aperture, item.Board)}*%");
                    definitions.AppendLine("%TD*%");
                    continue;
                }
                if (command.Length > 2 && command[0] == 'D' &&
                    int.TryParse(command.AsSpan(1, command.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var dcode))
                {
                    objects.AppendLine(apertureCodes.TryGetValue(dcode, out var mapped) ? $"D{mapped}*"
                        : throw Bad(item.File, $"정의되지 않은 아퍼처 D{dcode}입니다."));
                    continue;
                }
                if (Operation.Match(command) is { Success: true } operation)
                {
                    var g = operation.Groups;
                    x = g[2].Success ? Number(g[2].Value) / sx : x;
                    y = g[3].Success ? Number(g[3].Value) / sy : y;
                    if (x is null || y is null)
                        throw Bad(item.File, "첫 Gerber 작업의 X/Y 좌표가 모두 필요합니다.");
                    var point = item.Board.Transform(new CamPoint(x.Value, y.Value));
                    objects.Append(g[1].Value).Append('X').Append(Position(point.X)).Append('Y').Append(Position(point.Y));
                    if (g[4].Success || g[5].Success)
                    {
                        // I/J는 시작점에서 원호 중심까지의 벡터다. 회전해도 G02/G03 방향은 유지된다.
                        var center = item.Board.RotateVector(new(g[4].Success ? Number(g[4].Value) / sx : 0,
                            g[5].Success ? Number(g[5].Value) / sy : 0));
                        objects.Append('I').Append(Position(center.X)).Append('J').Append(Position(center.Y));
                    }
                    objects.AppendLine($"D0{g[6].Value}*");
                    continue;
                }
                // 한 보드의 넷 이름과 RefDes가 다른 보드에 연결되지 않게 한다.
                if (command.StartsWith("%TO.", StringComparison.Ordinal)) objectAttributes.Add(AttributeName(command));
                objects.AppendLine(PrefixObject(command, $"B{item.Index}_"));
            }
        }

        if (fiducials.Count > 0 && role is "Copper,L1,Top" or "Soldermask,Top")
        {
            var copper = role == "Copper,L1,Top";
            if (!copper && filePolarity != "Negative")
                throw new InvalidDataException("피두셜 개구를 추가하려면 음극성 상면 솔더마스크가 필요합니다.");
            if (copper) definitions.AppendLine("%TA.AperFunction,FiducialPad,Panel*%");
            definitions.AppendLine($"%ADD{nextDCode}C,{DecimalText(copper ? options.FiducialDiameter : options.MaskDiameter)}*%");
            definitions.AppendLine("%TD*%");
            objects.AppendLine("%TD*%").AppendLine("%LPD*%").AppendLine("G01*").AppendLine($"D{nextDCode}*");
            if (copper) objects.AppendLine("%TO.N,*%");
            foreach (var point in fiducials)
                objects.AppendLine($"X{Position(point.X)}Y{Position(point.Y)}D03*");
            objects.AppendLine("%TD*%");
        }

        var output = new StringBuilder().AppendLine("%TF.GenerationSoftware,GerberHandler,GerberHandler*%")
            .AppendLine("%TF.Part,Array*%").AppendLine($"%TF.FileFunction,{role}*%").AppendLine($"%TF.FilePolarity,{filePolarity}*%")
            .AppendLine($"%FSLAX{whole}{precision}Y{whole}{precision}*%").AppendLine("%MOMM*%")
            .Append(definitions).Append(objects).AppendLine("M02*");
        return new CamFile($"panel-{role.Replace(',', '-')}.gbr", role, output.ToString(), false);
    }

    private static CamFile MergeDrill(string role, IEnumerable<(CamFile File, MixedBoard Board, int Index)> sources,
        IReadOnlyList<CamPoint> tooling, IReadOnlyList<CamPoint> bites, PanelOptions options)
    {
        var plated = role.StartsWith("Plated,", StringComparison.Ordinal);
        var classification = plated ? "Plated,PTH" : "NonPlated,NPTH";
        var header = new StringBuilder().AppendLine("M48").AppendLine("; FORMAT={-:-/ absolute / metric / decimal}")
            .AppendLine("; #@! TF.GenerationSoftware,GerberHandler,GerberHandler").AppendLine($"; #@! TF.FileFunction,{role}")
            .AppendLine("FMAT,2").AppendLine("METRIC");
        var hits = new StringBuilder();
        var nextTool = 1;
        foreach (var (file, board, _) in sources)
        {
            var data = ReadDrill(file.Text);
            var toolFunctions = new Dictionary<int, string>();
            string? function = null;
            foreach (var line in data.Header)
            {
                if (line.StartsWith(DrillAttribute, StringComparison.Ordinal))
                {
                    function = line[DrillAttribute.Length..];
                    if (!function.StartsWith(classification + ",", StringComparison.Ordinal) && function != classification)
                        throw Bad(file, "드릴 도구의 도금 정보가 파일 역할과 다릅니다.");
                }
                else if (Tool.Match(line) is { Success: true } match && function is not null)
                {
                    toolFunctions.Add(GroupInt(match, 1), function);
                    function = null;
                }
            }
            foreach (var (id, diameter) in data.Tools)
                AddTool(diameter, toolFunctions.GetValueOrDefault(id), data.Hits.Where(hit => hit.Id == id)
                    .Select(hit => (board.Transform(hit.Point), hit.End is { } end ? (CamPoint?)board.Transform(end) : null)));
        }
        AddTool(options.ToolingDiameter, "NonPlated,NPTH,MechanicalDrill,Tooling", tooling.Select(point => (point, (CamPoint?)null)));
        AddTool(options.Routing?.BiteDiameter ?? 0, "NonPlated,NPTH,MechanicalDrill", bites.Select(point => (point, (CamPoint?)null)));
        return new CamFile(plated ? "panel-PTH.drl" : "panel-NPTH.drl", role,
            header.AppendLine("%").AppendLine("G90").AppendLine("G05").Append(hits).AppendLine("M30").ToString(), true);

        void AddTool(decimal diameter, string? function, IEnumerable<(CamPoint Point, CamPoint? End)> points)
        {
            var toolPoints = points.ToArray();
            if (toolPoints.Length == 0)
                return;
            if (function is not null)
                header.AppendLine(DrillAttribute + function);
            else
                header.AppendLine("; #@! TD.AperFunction");
            header.AppendLine($"T{nextTool}C{DecimalText(diameter)}");
            hits.AppendLine($"T{nextTool++}");
            foreach (var coordinate in toolPoints)
                WriteDrillHit(hits, coordinate.Point, coordinate.End);
        }
    }

    private static string ApertureDefinition(Match aperture, MixedBoard board)
    {
        var name = aperture.Groups[2].Value;
        var modifiers = aperture.Groups[3].Value;
        if (board.QuarterTurns == 0 || name == "C" || modifiers.Length == 0)
            return modifiers.Length == 0 ? name : $"{name},{modifiers}";

        var values = modifiers.Split('X');
        if ((name is "R" or "O") && board.QuarterTurns % 2 != 0)
            (values[0], values[1]) = (values[1], values[0]);
        else if (name == "P")
        {
            var angle = values.Length > 2 ? Number(values[2]) : 0;
            var rotated = DecimalText((angle % 360 - board.QuarterTurns * 90 + 720) % 360);
            if (values.Length == 2) values = [.. values, rotated];
            else values[2] = rotated;
        }
        else if (name == "RoundRect")
        {
            // 검증된 KiCad 매크로의 사각 코너 중심 좌표를 아퍼처 원점 기준으로 회전한다.
            for (var index = 1; index <= 7; index += 2)
            {
                var point = board.RotateVector(new(Number(values[index]), Number(values[index + 1])));
                values[index] = DecimalText(point.X);
                values[index + 1] = DecimalText(point.Y);
            }
        }
        return $"{name},{string.Join('X', values)}";
    }

    private static int GroupInt(Match match, int group) => int.Parse(match.Groups[group].ValueSpan, CultureInfo.InvariantCulture);
    private static string AttributeName(string command) => command[4..].Split(',', '*')[0];
    private static string DecimalText(decimal value) => value.ToString("0.0###########################", CultureInfo.InvariantCulture);
    private static string ExpandCommentAttribute(string command) => CommentAttribute.Match(command) is { Success: true } match
        ? $"%{match.Groups[1].Value}%" : command;
    private static InvalidDataException Bad(CamFile file, string message) => new($"{file.Name}: {message}");
}
