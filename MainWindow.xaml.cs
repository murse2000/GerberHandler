using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace GerberHandler;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string PresetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GerberHandler", "presets.json");
    private static readonly string LayerColorsPath = Path.Combine(Path.GetDirectoryName(PresetPath)!, "layer-colors.json");
    private static readonly Geometry EyeOpen = Geometry.Parse("M1 8 Q8 -1 15 8 Q8 17 1 8 M10 8 A2 2 0 1 1 6 8 A2 2 0 1 1 10 8");
    private static readonly Geometry EyeClosed = Geometry.Parse("M1 8 Q8 15 15 8 M2 2 L14 14");
    private Dictionary<string, string> layerColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> hiddenLayers = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Profile> profiles = [];
    private CamInput? source;
    private CamInput? saved;
    private Project? savedLayout;
    private IReadOnlyDictionary<string, string>? roleOverrides;
    private string? projectPath;
    private string? cleanProject;
    private string? lastInputPath;

    public MainWindow()
    {
        InitializeComponent();
        Viewer.FiducialsMoved += MoveFiducials;
        Run(() =>
        {
            if (File.Exists(PresetPath))
                profiles.AddRange(Read<List<Profile>>(PresetPath));
            SetProfiles(null);
            if (File.Exists(LayerColorsPath))
            {
                layerColors = new(Read<Dictionary<string, string>>(LayerColorsPath), StringComparer.OrdinalIgnoreCase);
                Viewer.SetLayerColors(layerColors);
            }
        });
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WindowLoaded;
        await UpdateService.CheckAsync(this, ConfirmCloseForUpdate);
    }

    private async void CheckForUpdates(object sender, RoutedEventArgs e) =>
        await UpdateService.CheckAsync(this, ConfirmCloseForUpdate, manual: true);

    private bool ConfirmCloseForUpdate() => ConfirmLeave();

    private void WindowClosing(object? sender, CancelEventArgs e) => e.Cancel = !ConfirmLeave();

    private void WindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void WindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths)
        {
            if (Path.GetExtension(paths[0]).Equals(".ghpanel", StringComparison.OrdinalIgnoreCase))
                LoadProject(paths[0]);
            else
                LoadSource(paths[0]);
        }
    }

    private void OpenZip(object sender, RoutedEventArgs e)
    {
        if (ChooseZip() is { } path)
            LoadSource(path);
    }

    private void OpenFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Gerber와 드릴 파일 폴더 열기" };
        if (dialog.ShowDialog(this) == true)
            LoadSource(dialog.FolderName);
    }

    private string? ChooseZip()
    {
        var dialog = new OpenFileDialog { Filter = "Gerber ZIP (*.zip)|*.zip" };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void LoadSource(string path, IReadOnlyDictionary<string, string>? overrides = null)
    {
        lastInputPath = path;
        if (!ConfirmLeave())
            return;

        Run(() =>
        {
            var input = CamEngine.Load(path, overrides);
            if (input.IsPanel)
            {
                savedLayout = null;
                OpenSaved(input);
                return;
            }

            roleOverrides = overrides;
            UseSource(input);
            projectPath = null;
            cleanProject = SnapshotText();
        });
    }

    private void MapLayers(object sender, RoutedEventArgs e)
    {
        var path = lastInputPath ?? source?.SourcePath ?? ChooseZip();
        if (path is null)
            return;

        var form = new StackPanel { Margin = new Thickness(14) };
        form.Children.Add(new TextBlock
        {
            Text = "한 줄에 파일 이름=Gerber 역할을 입력하세요. 파일에 선언된 역할이 없는 경우 적용합니다.\n예: board-Edge_Cuts.gbr=Profile,NP",
            TextWrapping = TextWrapping.Wrap
        });
        var input = new TextBox { AcceptsReturn = true, Height = 135, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        input.Text = string.Join(Environment.NewLine, roleOverrides?.Select(item => $"{item.Key}={item.Value}") ?? []);
        form.Children.Add(input);
        var apply = new Button { Content = "역할 적용", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        form.Children.Add(apply);
        var dialog = new Window { Title = "입력 파일 역할 지정", Owner = this, Content = form, Width = 470,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        apply.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true)
            return;

        Run(() =>
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in input.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 ||
                    !CamEngine.ValidRole(parts[1], Path.GetExtension(parts[0]).Equals(".drl", StringComparison.OrdinalIgnoreCase)) ||
                    !mapping.TryAdd(parts[0], parts[1]))
                    throw new InvalidDataException($"파일 이름과 역할을 확인하세요. {line}");
            }

            if (source is null || !string.Equals(Path.GetFullPath(path), source.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadSource(path, mapping);
                return;
            }

            var updated = CamEngine.Load(path, mapping);
            roleOverrides = mapping;
            UseSource(updated);
        });
    }

    private void UseSource(CamInput input)
    {
        source = input;
        saved = null;
        savedLayout = null;
        lastInputPath = input.SourcePath;
        Cells.ItemsSource = null;
        SourceLabel.Text = input.SourcePath;
        ShowFiles("원본 PCB", input.Files, input.Bounds);
    }

    private void ReplaceSource(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireSource();
        if (ChooseZip() is not { } path)
            return;

        var next = CamEngine.Load(path, roleOverrides);
        if (next.IsPanel)
            throw new InvalidDataException("원본에는 단일 PCB 제조 세트를 지정하세요.");

        UseSource(next);
        Preview();
    });

    private void OpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "GerberHandler 프로젝트 (*.ghpanel)|*.ghpanel" };
        if (dialog.ShowDialog(this) == true)
            LoadProject(dialog.FileName);
    }

    private void LoadProject(string path)
    {
        if (!ConfirmLeave())
            return;

        Run(() =>
        {
            var project = Read<Project>(path);
            var original = project.SourcePath;
            if (!File.Exists(original) && !Directory.Exists(original))
            {
                var dialog = new OpenFileDialog
                {
                    Title = "저장된 경로에 원본이 없습니다. 원본 ZIP을 선택하세요.",
                    Filter = "Gerber ZIP (*.zip)|*.zip"
                };
                if (dialog.ShowDialog(this) != true)
                    return;
                original = dialog.FileName;
            }

            var mapping = project.RoleOverrides is null ? null : new Dictionary<string, string>(project.RoleOverrides, StringComparer.OrdinalIgnoreCase);
            var input = CamEngine.Load(original, mapping);
            if (input.IsPanel)
                throw new InvalidDataException("프로젝트 원본으로 단일 PCB 제조 세트를 선택하세요.");

            SetProfiles(project.Profile);
            ApplyOptions(project.Options);
            roleOverrides = mapping;
            UseSource(input);
            projectPath = path;
            cleanProject = JsonSerializer.Serialize(project, JsonOptions);
            Preview();
        });
    }

    private void SaveProject(object sender, RoutedEventArgs e) => Run(() => SaveCurrentProject());

    private bool SaveCurrentProject()
    {
        var data = SnapshotText();
        var path = projectPath;
        if (path is null)
        {
            var dialog = new SaveFileDialog { Filter = "GerberHandler 프로젝트 (*.ghpanel)|*.ghpanel" };
            if (dialog.ShowDialog(this) != true)
                return false;
            path = dialog.FileName;
        }

        WriteAtomic(path, data);
        projectPath = path;
        cleanProject = data;
        StatusLabel.Text = $"프로젝트 저장 완료 · {Path.GetFileName(path)}";
        return true;
    }

    private bool ConfirmLeave()
    {
        if (source is null)
            return true;

        try
        {
            if (SnapshotText() == cleanProject)
                return true;
        }
        catch (InvalidDataException)
        {
            // 입력값이 잘못된 경우에도 변경한 작업의 종료 여부를 확인한다.
        }

        var result = MessageBox.Show(this, "변경한 패널 작업을 저장하시겠습니까?", "프로젝트 저장",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.No)
            return true;
        var savedProject = false;
        if (result == MessageBoxResult.Yes)
            Run(() => savedProject = SaveCurrentProject());
        return savedProject;
    }

    private void BuildPanel(object sender, RoutedEventArgs e) => Run(Preview);

    private void Preview() => RenderPreview(strictMarks: false);

    private void RenderPreview(bool strictMarks)
    {
        var project = Snapshot();
        var input = RequireSource();
        var problem = project.Options.Routing is null ? CamEngine.VCutOutlineProblem(input) : null;
        if (project.Options.Routing is null && (problem is not null || project.Options.HasGap && !project.Options.FillGaps))
        {
            var (boards, layoutBounds) = CamEngine.PlacementBounds(input, project.Options);
            IReadOnlyList<CamPoint> fiducials = [];
            string? markProblem = null;
            try { fiducials = CamEngine.LayoutFiducials(input, boards, layoutBounds, project.Options); }
            catch (InvalidDataException e) when (!strictMarks) { markProblem = e.Message; }
            Viewer.SetLayout(input, project.Options, boards, layoutBounds, fiducials);
            Viewer.SetEditableFiducials(fiducials);
            ShowLayers("배열 미리보기", input.Files, layoutBounds);
            StatusLabel.Text = $"{project.Options.Rows * project.Options.Columns}장 · {Size(layoutBounds)} · " +
                (problem ?? "열린 간격의 제조 ZIP에는 탭 라우팅이 필요합니다.");
            if (markProblem is not null)
                Findings.ItemsSource = new[] { new PanelFinding(markProblem, null) };
            return;
        }

        var panel = CamEngine.Build(input, project.Options);
        ShowPanel("편집 중인 패널", panel, input, project.Options);
        Viewer.SetEditableFiducials(panel.Fiducials);
        SetChecks(panel, project.Profile, false);
        var marks = string.Join(" / ", panel.Fiducials.Select(p => $"({Number(p.X)}, {Number(p.Y)})"));
        StatusLabel.Text = $"{project.Options.Rows * project.Options.Columns}장 · {Size(panel.Bounds)}" +
            (panel.Tabs == 0 ? "" : $" · 연결 탭 {panel.Tabs}개 · 마우스바이트 {panel.MouseBites}개") +
            (marks.Length == 0 ? "" : $" · 피두셜 {marks}");
    }

    private void MoveFiducials(IReadOnlyList<CamPoint> points) => Run(() =>
    {
        var previous = FiducialPositions.Text;
        FiducialPositions.Text = FormatPositions(points);
        try { RenderPreview(strictMarks: true); }
        catch
        {
            FiducialPositions.Text = previous;
            throw;
        }
    });

    private void SavePanel(object sender, RoutedEventArgs e) => Run(() =>
    {
        var project = Snapshot();
        var input = RequireSource();
        var panel = CamEngine.Build(input, project.Options);
        ShowPanel("편집 중인 패널", panel, input, project.Options);
        if (!SetChecks(panel, project.Profile, true))
            return;
        if (panel.Warnings.Count > 0 && MessageBox.Show(this, string.Join(Environment.NewLine, panel.Warnings) +
                "\n\n원본에 가공 홀이 없음을 확인했고 저장하시겠습니까?", "드릴 파일 확인", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var dialog = new SaveFileDialog { Filter = "패널 제조 ZIP (*.zip)|*.zip", FileName = "panel.zip" };
        if (dialog.ShowDialog(this) != true)
            return;

        if (string.Equals(Path.GetFullPath(project.SourcePath), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("제조 ZIP은 원본과 다른 이름으로 저장하세요.");

        var temporary = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, $"{Guid.NewGuid():N}.zip");
        try
        {
            CamEngine.Export(panel, temporary);
            var checkedOutput = CamEngine.Load(temporary);
            if (!checkedOutput.IsPanel || checkedOutput.Bounds != panel.Bounds)
                throw new InvalidDataException("출력의 패널 외곽이 미리보기와 다릅니다.");
            File.Move(temporary, dialog.FileName, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        savedLayout = project;
        OpenSaved(CamEngine.Load(dialog.FileName));
    });

    private void ReviewZip(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (ChooseZip() is { } path)
        {
            savedLayout = null;
            OpenSaved(CamEngine.Load(path));
        }
    });

    private void OpenSaved(CamInput input)
    {
        if (!input.IsPanel)
            throw new InvalidDataException("GerberHandler에서 출력한 패널 제조 ZIP을 선택하세요.");

        saved = input;
        var title = $"저장본 · {Path.GetFileName(input.SourcePath)}";
        if (source is { } original && savedLayout is { } layout)
        {
            var (boards, _) = CamEngine.PlacementBounds(original, layout.Options);
            Viewer.SetPanel(input.Files, input.Bounds, original.Bounds, boards, layout.Options);
            ShowLayers(title, input.Files, input.Bounds);
        }
        else
            ShowFiles(title, input.Files, input.Bounds);
        if (source is not null)
        {
            var options = savedLayout?.Options ?? ReadOptions();
            Cells.ItemsSource = Enumerable.Range(0, options.Rows)
                .SelectMany(row => Enumerable.Range(0, options.Columns).Select(col => new Cell(row, col)))
                .ToArray();
            Cells.SelectedIndex = 0;
        }
    }

    private void ShowSource(object sender, RoutedEventArgs e) => Run(() =>
    {
        var input = RequireSource();
        ShowFiles("원본 PCB", input.Files, input.Bounds);
    });

    private void ShowExport(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (saved is null)
            throw new InvalidDataException("저장한 패널 ZIP을 열거나 먼저 패널을 저장하세요.");
        OpenSaved(saved);
    });

    private void ShowFiles(string title, IReadOnlyList<CamFile> files, CamBounds bounds)
    {
        Viewer.SetData(files, bounds);
        ShowLayers(title, files, bounds);
    }

    private void ShowPanel(string title, CamPanel panel, CamInput input, PanelOptions options)
    {
        var (boards, _) = CamEngine.PlacementBounds(input, options);
        Viewer.SetPanel(panel.Files, panel.Bounds, input.Bounds, boards, options);
        ShowLayers(title, panel.Files, panel.Bounds);
    }

    private void ShowLayers(string title, IReadOnlyList<CamFile> files, CamBounds bounds)
    {
        LayerList.Children.Clear();
        foreach (var group in files.GroupBy(file => file.Role))
        {
            var role = group.Key;
            var icon = new System.Windows.Shapes.Path { Width = 15, Height = 15, Stretch = Stretch.Uniform,
                Stroke = Brushes.DarkSlateGray, StrokeThickness = 1.3 };
            var toggle = new ToggleButton
            {
                Content = icon, IsChecked = !hiddenLayers.Contains(role), Width = 27, Height = 24,
                Margin = new Thickness(0, 2, 4, 2)
            };
            void RefreshIcon()
            {
                var showing = toggle.IsChecked == true;
                icon.Data = showing ? EyeOpen : EyeClosed;
                toggle.ToolTip = showing ? $"{role} 숨기기" : $"{role} 보이기";
                System.Windows.Automation.AutomationProperties.SetName(toggle, (string)toggle.ToolTip);
            }
            RefreshIcon();
            toggle.Checked += (_, _) => RefreshIcon();
            toggle.Unchecked += (_, _) => RefreshIcon();
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked == true) hiddenLayers.Remove(role);
                else hiddenLayers.Add(role);
                UpdateLayers();
            };
            var row = new DockPanel { Tag = role };
            DockPanel.SetDock(toggle, Dock.Left);
            row.Children.Add(toggle);
            row.Children.Add(new TextBlock { Text = role, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = string.Join(Environment.NewLine, group.Select(file => file.Name)) });
            LayerList.Children.Add(row);
        }

        ViewLabel.Text = $"{title} · {Size(bounds)}";
        StatusLabel.Text = $"{files.Count}개 제조 파일";
        Findings.ItemsSource = null;
        UpdateLayers();
    }

    private void UpdateLayers() => Viewer.SetVisibleRoles(
        LayerList.Children.OfType<DockPanel>().Select(row => (string)row.Tag).Where(role => !hiddenLayers.Contains(role)));

    private void ShowAllLayers(object sender, RoutedEventArgs e) => SetAllLayers(true);

    private void HideAllLayers(object sender, RoutedEventArgs e) => SetAllLayers(false);

    private void SetAllLayers(bool showing)
    {
        foreach (var row in LayerList.Children.OfType<DockPanel>())
        {
            var role = (string)row.Tag;
            if (showing) hiddenLayers.Remove(role);
            else hiddenLayers.Add(role);
            ((ToggleButton)row.Children[0]).IsChecked = showing;
        }
        UpdateLayers();
    }

    private void EditLayerColors(object sender, RoutedEventArgs e) => Run(() =>
    {
        var roles = LayerList.Children.OfType<DockPanel>().Select(row => (string)row.Tag).Concat(layerColors.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (roles.Length == 0)
            throw new InvalidDataException("색상을 지정할 제조 레이어를 먼저 여세요.");
        if (LayerStyleEditor.Show(this, roles, layerColors) is not { } settings)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(LayerColorsPath)!);
        WriteAtomic(LayerColorsPath, JsonSerializer.Serialize(settings, JsonOptions));
        layerColors = settings;
        Viewer.SetLayerColors(settings);
    });

    private void CompareCell(object sender, RoutedEventArgs e) => Run(() =>
    {
        var original = RequireSource();
        var output = saved ?? throw new InvalidDataException("비교할 패널 ZIP을 먼저 여세요.");
        var cell = Cells.SelectedItem as Cell ?? throw new InvalidDataException("비교할 배열 셀을 선택하세요.");
        var options = savedLayout?.Options ?? ReadOptions();
        var (boards, expected) = CamEngine.PlacementBounds(original, options);
        if (output.Bounds != expected)
            throw new InvalidDataException("ZIP 외곽이 현재 원본과 배열·레일 설정에 맞지 않습니다.");

        var diff = PanelComparison.Compare(original, output, cell.Row, cell.Column, options);
        Viewer.SetPanel(output.Files, output.Bounds, original.Bounds, boards, options);
        ShowLayers($"저장본 비교 · {cell}", output.Files, output.Bounds);
        Viewer.ShowComparison(diff.Missing, diff.Added);
        Findings.ItemsSource = diff.Findings;
        StatusLabel.Text = $"{cell} · 원본 형상 차이 {diff.Missing.Count + diff.Added.Count}개";
    });

    private void FindingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Findings.SelectedItem is PanelFinding { Location: { } location })
            Viewer.Focus(location);
    }

    private void CreatePreset(object sender, RoutedEventArgs e) => Run(() =>
    {
        var profile = ProfileEditor.Show(this, FiducialDiameter.Text, MaskDiameter.Text, ToolingDiameter.Text);
        if (profile is null)
            return;

        var next = profiles.Where(item => !string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase))
            .Append(profile).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(PresetPath)!);
        WriteAtomic(PresetPath, JsonSerializer.Serialize(next, JsonOptions));
        profiles.Clear();
        profiles.AddRange(next);
        SetProfiles(profile);
    });

    private void SetProfiles(Profile? selected)
    {
        var choices = profiles.ToList();
        choices.Insert(0, Profile.Reference);
        if (selected is not null && !choices.Contains(selected))
            choices.Add(selected);
        Presets.ItemsSource = choices;
        Presets.SelectedItem = selected;
    }

    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetLabel is null)
            return;
        if (Presets.SelectedItem is Profile profile)
        {
            PresetLabel.Text = $"{profile.Source} · {Number(profile.MinWidth)}–{Number(profile.MaxWidth)} × " +
                $"{Number(profile.MinHeight)}–{Number(profile.MaxHeight)} mm · 가공 여유 {Number(profile.Clearance)} mm";
            FiducialDiameter.Text = Number(profile.FiducialDiameter);
            MaskDiameter.Text = Number(profile.MaskDiameter);
            ToolingDiameter.Text = Number(profile.ToolingDiameter);
        }
        else
        {
            PresetLabel.Text = "제조 ZIP을 저장하려면 확인한 제조 조건을 선택하세요.";
        }
    }

    private bool SetChecks(CamPanel panel, Profile? profile, bool forExport)
    {
        var messages = new List<PanelFinding>();
        messages.AddRange(panel.Warnings.Select(warning => new PanelFinding(warning, null)));
        var errors = profile?.Validate(panel, ReadOptions()).ToArray() ?? [];
        messages.AddRange(errors);
        if (forExport && profile is null)
            messages.Add(new PanelFinding("확인한 제조·조립 프리셋을 먼저 등록하고 선택하세요.", null));
        Findings.ItemsSource = messages;
        if (forExport && (profile is null || errors.Length != 0))
        {
            StatusLabel.Text = "검사 항목을 확인한 뒤 제조 ZIP을 저장하세요.";
            return false;
        }
        return true;
    }

    private CamInput RequireSource() => source ?? throw new InvalidDataException("단일 PCB Gerber를 먼저 여세요.");

    private Project Snapshot() => new(RequireSource().SourcePath, ReadOptions(), Presets.SelectedItem as Profile, roleOverrides);

    private string SnapshotText() => JsonSerializer.Serialize(Snapshot(), JsonOptions);

    private PanelOptions ReadOptions() => new(PositiveInteger(Rows), PositiveInteger(Columns),
        ReadRail(EnableLeftRail, RailLeft), ReadRail(EnableRightRail, RailRight),
        ReadRail(EnableBottomRail, RailBottom), ReadRail(EnableTopRail, RailTop),
        Fiducials.IsChecked == true, ReadNumber(FiducialDiameter.Text), ReadNumber(MaskDiameter.Text),
        Tooling.IsChecked == true, ReadNumber(ToolingDiameter.Text), (Presets.SelectedItem as Profile)?.Clearance ?? 0m)
    {
        FiducialPositions = ParsePositions(FiducialPositions.Text),
        ToolingPositions = ParsePositions(ToolingPositions.Text),
        RowGap = ReadNumber(RowGap.Text),
        ColumnGap = ReadNumber(ColumnGap.Text),
        FillGaps = TabRouting.IsChecked != true && FillGaps.IsChecked == true,
        Routing = TabRouting.IsChecked == true ? new RoutingOptions(ReadNumber(RailChannel.Text), ReadNumber(CutterDiameter.Text),
            ReadNumber(TabWidth.Text), PositiveInteger(TabsPerEdge), ReadNumber(BiteDiameter.Text), ReadNumber(BitePitch.Text)) : null
    };

    private void ApplyOptions(PanelOptions options)
    {
        Rows.Text = Number(options.Rows);
        Columns.Text = Number(options.Columns);
        RowGap.Text = Number(options.RowGap);
        ColumnGap.Text = Number(options.ColumnGap);
        FillGaps.IsChecked = options.FillGaps;
        TabRouting.IsChecked = options.Routing is not null;
        if (options.Routing is { } routing)
        {
            RailChannel.Text = Number(routing.RailChannel);
            CutterDiameter.Text = Number(routing.CutterDiameter);
            TabWidth.Text = Number(routing.TabWidth);
            TabsPerEdge.Text = Number(routing.TabsPerEdge);
            BiteDiameter.Text = Number(routing.BiteDiameter);
            BitePitch.Text = Number(routing.BitePitch);
        }
        SetRail(EnableLeftRail, RailLeft, options.RailLeft);
        SetRail(EnableRightRail, RailRight, options.RailRight);
        SetRail(EnableBottomRail, RailBottom, options.RailBottom);
        SetRail(EnableTopRail, RailTop, options.RailTop);
        Fiducials.IsChecked = options.Fiducials;
        FiducialDiameter.Text = Number(options.FiducialDiameter);
        MaskDiameter.Text = Number(options.MaskDiameter);
        Tooling.IsChecked = options.Tooling;
        ToolingDiameter.Text = Number(options.ToolingDiameter);
        FiducialPositions.Text = FormatPositions(options.FiducialPositions);
        ToolingPositions.Text = FormatPositions(options.ToolingPositions);
    }

    private static decimal ReadRail(CheckBox enabled, TextBox size) => enabled.IsChecked == true ? ReadNumber(size.Text) : 0;

    private static void SetRail(CheckBox enabled, TextBox size, decimal value)
    {
        enabled.IsChecked = value != 0;
        size.Text = value == 0 ? "5" : Number(value);
    }

    internal static decimal ReadNumber(string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException($"숫자 입력을 확인하세요. '{text}'");

    private static int PositiveInteger(TextBox box) => int.TryParse(box.Text, out var value) && value is >= 1 and <= 100
        ? value : throw new InvalidDataException("행과 열에는 1부터 100까지의 정수를 입력하세요.");

    private static CamPoint[]? ParsePositions(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            var coordinates = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (coordinates.Length != 2)
                throw new InvalidDataException("마크 좌표는 한 줄에 X Y 두 값을 입력하세요.");
            return new CamPoint(ReadNumber(coordinates[0]), ReadNumber(coordinates[1]));
        }).ToArray();
    }

    private static string FormatPositions(IReadOnlyList<CamPoint>? points) => points is null ? "" :
        string.Join(Environment.NewLine, points.Select(point => $"{Number(point.X)} {Number(point.Y)}"));

    internal static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Size(CamBounds bounds) => $"{Number(bounds.Width)} × {Number(bounds.Height)} mm";

    private static T Read<T>(string path) where T : class => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("저장된 설정을 읽을 수 없습니다.");

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException or OverflowException or NotSupportedException)
        {
            StatusLabel.Text = error.Message;
            MessageBox.Show(this, error.Message, "GerberHandler", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed record Project(string SourcePath, PanelOptions Options, Profile? Profile, IReadOnlyDictionary<string, string>? RoleOverrides);

    private sealed record Cell(int Row, int Column)
    {
        public override string ToString() => $"{Row + 1}행 {Column + 1}열";
    }
}
