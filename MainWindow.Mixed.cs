using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GerberHandler;

public partial class MainWindow
{
    private IReadOnlyList<MixedBoard> mixedBoards = [];
    private IReadOnlyList<MixedBoard>? savedMixedBoards;

    private void AddBoardZips(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "패널에 넣을 단일 PCB Gerber ZIP 선택", Filter = "Gerber ZIP (*.zip)|*.zip", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            Run(() => AddMixedBoards(dialog.FileNames));
    }

    private void AddBoardFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "패널에 넣을 단일 PCB 제조 파일 폴더 선택" };
        if (dialog.ShowDialog(this) == true)
            Run(() => AddMixedBoards([dialog.FolderName]));
    }

    private void AddMixedBoards(IReadOnlyList<string> paths)
    {
        var next = mixedBoards.ToList();
        if (next.Count == 0 && source is { } first)
            next.Add(new MixedBoard(first, new CamPoint(first.Bounds.MinX, first.Bounds.MinY)) { RoleOverrides = roleOverrides });
        if (next.Count + paths.Count > MixedPanel.MaxBoards)
            throw new InvalidDataException($"한 패널에 보드를 {MixedPanel.MaxBoards}개까지 넣을 수 있습니다.");
        var gap = ReadMixedGap();
        foreach (var path in paths)
        {
            var input = CamEngine.Load(path);
            if (input.IsPanel)
                throw new InvalidDataException($"'{Path.GetFileName(path)}'은 이미 패널입니다. 단일 PCB 제조 세트를 선택하세요.");
            var origin = next.Count == 0 ? new CamPoint(0, 0)
                : new CamPoint(next.Max(board => board.Bounds.MaxX) + gap, next.Min(board => board.Bounds.MinY));
            next.Add(new MixedBoard(input, origin));
        }
        CommitMixed(next, next.Count - 1);
    }

    private decimal ReadMixedGap()
    {
        var gap = ReadNumber(MixedGap.Text);
        if (gap < 0 || gap > 10000 || gap != decimal.Round(gap, 6))
            throw new InvalidDataException("자동 정렬 간격은 0~10000 mm와 소수점 여섯 자리까지 입력하세요.");
        return gap;
    }

    private void ArrangeMixedHorizontal(object sender, RoutedEventArgs e) => Run(() => ArrangeMixed(horizontal: true));

    private void ArrangeMixedVertical(object sender, RoutedEventArgs e) => Run(() => ArrangeMixed(horizontal: false));

    private void AutoArrangeMixed(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (mixedBoards.Count > 1)
            CommitMixed(MixedPanel.AutoArrange(mixedBoards, ReadMixedGap(), ReadOptions(), Presets.SelectedItem as Profile),
                MixedBoardList.SelectedIndex);
    });

    private void ArrangeMixed(bool horizontal)
    {
        if (mixedBoards.Count == 0)
            return;
        var gap = ReadMixedGap();
        var anchor = new CamPoint(mixedBoards.Min(board => board.Bounds.MinX), mixedBoards.Min(board => board.Bounds.MinY));
        var next = new List<MixedBoard>();
        var travel = 0m;
        foreach (var board in mixedBoards)
        {
            next.Add(board with { Origin = horizontal ? anchor with { X = anchor.X + travel } : anchor with { Y = anchor.Y + travel } });
            travel += (horizontal ? board.Bounds.Width : board.Bounds.Height) + gap;
        }
        CommitMixed(next, MixedBoardList.SelectedIndex);
    }

    private void RemoveMixedBoard(object sender, RoutedEventArgs e) => Run(() =>
    {
        var selected = MixedBoardList.SelectedIndex;
        if (selected < 0)
            return;
        if (mixedBoards.Count == 1)
            throw new InvalidDataException("보드를 교체하려면 새 보드를 먼저 추가하세요.");
        var next = mixedBoards.Where((_, index) => index != selected).ToArray();
        CommitMixed(next, Math.Min(selected, next.Length - 1));
    });

    private void MoveMixedBoard(int index, CamPoint origin) => Run(() =>
    {
        if (index < 0 || index >= mixedBoards.Count)
            return;
        var next = mixedBoards.ToArray();
        next[index] = next[index] with { Origin = origin };
        CommitMixed(next, index);
    });

    private void RotateMixedBoard(object sender, RoutedEventArgs e) => Run(() =>
    {
        var index = MixedBoardList.SelectedIndex;
        if (index < 0)
        {
            StatusLabel.Text = "2D 화면이나 혼합 배치 목록에서 회전할 보드를 선택하세요.";
            return;
        }
        var next = mixedBoards.ToArray();
        next[index] = next[index] with { QuarterTurns = (next[index].QuarterTurns + 1) % 4 };
        CommitMixed(next, index);
    });

    private void CommitMixed(IReadOnlyList<MixedBoard> next, int selection)
    {
        var options = ReadOptions();
        _ = MixedPanel.Placement(next, options);
        RenderMixed(next, options, selection);
        mixedBoards = next;
        source = next[0].Input;
        roleOverrides = next[0].RoleOverrides;
        lastInputPath = source.SourcePath;
        RefreshMixedControls(selection);
    }

    private void RefreshMixedControls(int selection)
    {
        var mixing = mixedBoards.Count > 0;
        MixedControls.Visibility = mixing ? Visibility.Visible : Visibility.Collapsed;
        MixedBoardList.ItemsSource = mixedBoards;
        MixedBoardList.SelectedIndex = selection;
        SingleRows.IsEnabled = SingleColumns.IsEnabled = SingleRowGap.IsEnabled = SingleColumnGap.IsEnabled = !mixing;
        CompareControls.IsEnabled = !mixing;
        if (mixing)
        {
            Cells.ItemsSource = null;
            SourceLabel.Text = $"혼합 배치 · {mixedBoards.Count}개 PCB";
        }
    }

    private void MixedSelectionChanged(object sender, SelectionChangedEventArgs e) => Viewer.SelectBoard(MixedBoardList.SelectedIndex);

    private void RoutingModeChanged(object sender, RoutedEventArgs e)
    {
        if (mixedBoards.Count > 0)
            Run(Preview);
    }

    private void RenderMixed(IReadOnlyList<MixedBoard> boards, PanelOptions options, int selection, bool strictMarks = false)
    {
        var (bounds, panelBounds) = MixedPanel.Placement(boards, options);
        IReadOnlyList<CamPoint> fiducials = [];
        string? issue = null;
        try { (fiducials, _) = MixedPanel.RailMarks(options, bounds, panelBounds); }
        catch (InvalidDataException error) when (!strictMarks) { issue = error.Message; }
        CamPanel? output = null;
        RoutedPanel? route = null;
        if (options.Routing is { } routing)
        {
            try
            {
                route = MixedRouting.Build(boards, options, routing, bounds, panelBounds);
                if (issue is null)
                    output = MixedPanel.BuildWithRoute(boards, options, route);
            }
            catch (InvalidDataException error) { issue ??= error.Message; }
        }
        if (output is not null)
        {
            Viewer.SetMixedOutput(output.Files, panelBounds, bounds, options);
            ShowLayers("혼합 패널 미리보기", output.Files, panelBounds);
            SetChecks(output, Presets.SelectedItem as Profile, false);
        }
        else
        {
            var layers = Viewer.SetMixedLayout(boards, bounds, panelBounds, options, fiducials, route);
            ShowLayers(route is null ? "혼합 배치 미리보기" : "혼합 라우팅 미리보기", layers, panelBounds);
            if (issue is not null)
                Findings.ItemsSource = new[] { new PanelFinding(issue, null) };
        }
        Viewer.SetEditableBoards(boards, selection);
        Viewer.SetEditableFiducials(fiducials);
        var details = output is not null ? $"연결 탭 {output.Tabs}개 · 마우스바이트 {output.MouseBites}개"
            : route is not null ? $"미리보기 · 연결 탭 {route.TabCount}개 · 마우스바이트 {route.Bites.Count}개 · 제조 ZIP 저장 불가 · {issue}"
            : issue ?? "제조 ZIP을 만들려면 탭 라우팅을 선택하세요.";
        StatusLabel.Text = $"혼합 {boards.Count}장 · {Size(panelBounds)} · {details}";
    }

    private void LoadMixedProject(Project project, string path)
    {
        var boards = project.Boards!.Select(item => new MixedBoard(CamEngine.Load(item.SourcePath, item.RoleOverrides), item.Origin)
        {
            RoleOverrides = item.RoleOverrides,
            QuarterTurns = item.QuarterTurns
        }).ToArray();
        _ = MixedPanel.Placement(boards, project.Options);
        SetProfiles(project.Profile);
        ApplyOptions(project.Options);
        MixedGap.Text = Number(project.MixedGap);
        CommitMixed(boards, 0);
        projectPath = path;
        saved = null;
        savedLayout = null;
        savedMixedBoards = null;
        cleanProject = SnapshotText();
    }

    private sealed record MixedPlacement(string SourcePath, CamPoint Origin, IReadOnlyDictionary<string, string>? RoleOverrides, int QuarterTurns = 0);
}
