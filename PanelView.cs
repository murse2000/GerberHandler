using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace GerberHandler;

public sealed class PanelView : UserControl
{
    private static readonly Brush CanvasColor = BrushOf(18, 25, 32);
    private static readonly Brush BoardColor = BrushOf(26, 46, 48);
    private static readonly Brush SurfaceColor = BrushOf(28, 94, 74);
    private static readonly Brush GuideColor = BrushOf(45, 117, 126);
    private static readonly Brush FiducialColor = BrushOf(251, 225, 138);
    private static readonly Brush CopperColor = BrushOf(219, 164, 77);
    private static readonly Brush ProfileColor = BrushOf(160, 193, 205);
    private static readonly Brush CutColor = BrushOf(239, 170, 94);
    private static readonly Brush TextColor = BrushOf(219, 230, 237);
    private readonly CanvasSurface canvas;
    private readonly Viewport3D viewport = new();
    private readonly OrthographicCamera camera = new();
    private readonly ModelVisual3D scene = new();
    private readonly TabControl tabs = new();
    private readonly TextBlock measuringResult = new() { Margin = new Thickness(8, 2, 0, 0), Foreground = TextColor };
    private readonly TextBox thicknessField = new() { Width = 64, Text = "1.60" };
    private readonly TextBlock thicknessError = new() { Margin = new Thickness(8, 2, 0, 0), Foreground = TextColor };
    private readonly System.Windows.Controls.Primitives.ToggleButton measure = new() { Content = "거리 측정", Margin = new Thickness(3, 0, 3, 0) };
    private IReadOnlyList<Layer> layers = [];
    private IReadOnlyList<(Geometry Geometry, Brush Brush)> differences = [];
    private HashSet<string> visible = new(StringComparer.OrdinalIgnoreCase);
    private Rect board;
    private IReadOnlyList<IReadOnlyList<CamPoint>> outline = [];
    private Geometry substrate = Geometry.Empty;
    private IReadOnlyList<Rect> guideAreas = [];
    private IReadOnlyDictionary<string, string> layerColors = new Dictionary<string, string>();
    private double zoomFactor = 1;
    private Vector pan;
    private Point? dragStart;
    private Vector dragOffset;
    private IReadOnlyList<CamPoint> editableFiducials = [];
    private int draggedFiducial = -1;
    private CamPoint? dragTarget;
    internal event Action<IReadOnlyList<CamPoint>>? FiducialsMoved;
    private IReadOnlyList<MixedBoard> editableBoards = [];
    private int selectedBoard = -1;
    private int draggedBoard = -1;
    internal event Action<int>? BoardSelected;
    internal event Action<int, CamPoint>? BoardMoved;
    private readonly System.Windows.Controls.Primitives.ToggleButton addTab = new()
    {
        Content = "탭 추가", Margin = new Thickness(3, 0, 3, 0), ToolTip = "보드 사이 또는 보드와 레일 사이의 빈 공간을 클릭하세요."
    };
    private readonly Button deleteTab;
    private readonly Button resetTabs;
    private IReadOnlyList<MixedTab> editableTabs = [];
    private decimal tabHalfWidth;
    private int selectedTab = -1;
    private int draggedTab = -1;
    private MixedTab? tabTarget;
    internal event Action<IReadOnlyList<MixedTab>?, CamPoint?>? TabsEdited;
    private Point? measureStart;
    private Point? measureEnd;
    private bool hasData;
    private bool sceneDirty;
    private double boardThickness = 1.6;
    private double azimuth = -75;
    private double elevation = 50;
    private double orbitZoom = 1;

    public PanelView()
    {
        canvas = new CanvasSurface(DrawPlan) { ClipToBounds = true };
        deleteTab = Button("탭 삭제", () => TabsEdited?.Invoke(editableTabs.Where((_, index) => index != selectedTab).ToArray(), null));
        resetTabs = Button("자동 탭 복원", () => TabsEdited?.Invoke(null, null));
        SetEditableTabs(null, 0, false);
        var planPage = CreatePage(canvas, Button("전체 보기", FitPlan), measure, addTab, deleteTab, resetTabs, measuringResult);
        viewport.ClipToBounds = true;
        viewport.Camera = camera;
        viewport.Children.Add(scene);
        var orbitPage = CreatePage(viewport,
            Button("위에서 보기", () => SetOrbit(-90, 89.9)),
            Button("아래에서 보기", () => SetOrbit(-90, -89.9)),
            Button("사선 보기", () => SetOrbit(-75, 50)),
            new TextBlock { Text = "표시 두께(mm)", Margin = new Thickness(10, 2, 6, 0), Foreground = TextColor },
            thicknessField, thicknessError);
        tabs.Items.Add(new TabItem { Header = "2D 레이어", Content = planPage });
        tabs.Items.Add(new TabItem { Header = "3D 패널", Content = orbitPage });
        tabs.SelectionChanged += (_, _) => RefreshSceneIfVisible();
        Content = tabs;

        canvas.MouseWheel += ZoomPlan;
        canvas.MouseLeftButtonDown += BeginPlanGesture;
        canvas.MouseLeftButtonUp += EndPlanGesture;
        canvas.LostMouseCapture += (_, _) => ClearPlanDrag();
        canvas.MouseMove += MovePlanGesture;
        measure.Unchecked += (_, _) => ClearMeasurement();
        measure.Checked += (_, _) => addTab.IsChecked = false;
        addTab.Checked += (_, _) => measure.IsChecked = false;
        thicknessField.LostKeyboardFocus += (_, _) => CommitThickness();
        thicknessField.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitThickness(); };
        viewport.MouseLeftButtonDown += (_, e) => { dragStart = e.GetPosition(viewport); viewport.CaptureMouse(); };
        viewport.MouseLeftButtonUp += (_, _) => { dragStart = null; viewport.ReleaseMouseCapture(); };
        viewport.MouseMove += RotateScene;
        viewport.MouseWheel += (_, e) =>
        {
            orbitZoom = Math.Clamp(orbitZoom * Math.Pow(1.12, e.Delta / 120.0), 0.5, 30);
            UpdateCamera();
            e.Handled = true;
        };
        viewport.SizeChanged += (_, _) => UpdateCamera();
    }

    public void SetData(IReadOnlyList<CamFile> files, CamBounds bounds) => SetData(files, bounds, []);

    internal void SetPanel(IReadOnlyList<CamFile> files, CamBounds panel, CamBounds source, CamBounds boards, PanelOptions options) =>
        SetData(files, panel, LayoutFillAreas(source, boards, panel, options));

    internal void SetMixedOutput(IReadOnlyList<CamFile> files, CamBounds panel, CamBounds boards, PanelOptions options) =>
        SetData(files, panel, LayoutFillAreas(boards, boards, panel, options with { Rows = 1, Columns = 1, FillGaps = false }));

    internal IReadOnlyList<CamFile> SetMixedLayout(IReadOnlyList<MixedBoard> boards, CamBounds content, CamBounds panel, PanelOptions options,
        IReadOnlyList<CamPoint> fiducials, RoutedPanel? route)
    {
        var frame = LayoutFillAreas(content, content, panel, options with { Rows = 1, Columns = 1, FillGaps = false });
        var contours = new List<IReadOnlyList<CamPoint>>();
        var geometry = new Dictionary<string, (CamFile File, List<CamShape> Shapes)>();
        foreach (var board in boards)
            foreach (var file in board.Input.Files)
            {
                var source = CamEngine.Geometry(file);
                var translated = source.Select(board.Transform).ToArray();
                if (file.Role.StartsWith("Profile", StringComparison.Ordinal))
                {
                    if (route is not null)
                        continue;
                    contours.AddRange(CamEngine.OutlineContours(translated));
                }
                if (!geometry.TryGetValue(file.Role, out var layer))
                    geometry.Add(file.Role, layer = (file, []));
                layer.Shapes.AddRange(translated);
            }
        if (route is not null)
        {
            // 레이어 병합에 실패해도 검증된 라우팅 외곽과 타공은 실제 좌표로 표시한다.
            var profile = CamEngine.LineFile("라우팅 외곽 미리보기", "Profile,NP", route.Profile);
            var shapes = CamEngine.Geometry(profile).ToList();
            geometry.Add(profile.Role, (profile, shapes));
            contours.AddRange(CamEngine.OutlineContours(shapes));
            if (route.Bites.Count > 0)
            {
                var bites = new CamFile("비도금 타공 · 미리보기", "마우스바이트 미리보기", "", true);
                var diameter = options.Routing!.BiteDiameter;
                geometry.Add(bites.Role, (bites, route.Bites.Select(point => new CamShape("Drill", [point], diameter, diameter, true, true)).ToList()));
            }
        }
        else
            contours.AddRange(frame.Select(RectangleContour));
        if (geometry.TryGetValue("Copper,L1,Top", out var copper))
            copper.Shapes.AddRange(fiducials.Select(point => new CamShape("Flash", [point], options.FiducialDiameter,
                options.FiducialDiameter, true, true, true)));
        if (geometry.TryGetValue("Soldermask,Top", out var mask) && CamEngine.GerberAttribute(mask.File.Text, "FilePolarity") == "Negative")
            mask.Shapes.AddRange(fiducials.Select(point => new CamShape("Flash", [point], options.MaskDiameter, options.MaskDiameter, true, true)));
        var extent = ToRect(panel);
        ApplyData(extent, geometry.Values.Select(layer => BuildLayer(layer.File, layer.Shapes, extent)).ToArray(), contours, frame);
        return geometry.Values.Select(layer => layer.File).ToArray();
    }

    private void SetData(IReadOnlyList<CamFile> files, CamBounds bounds, IReadOnlyList<CamBounds> fills)
    {
        var nextBoard = ToRect(bounds);
        var nextLayers = files.Select(file => BuildLayer(file, CamEngine.Geometry(file), nextBoard)).ToArray();
        var nextOutline = CamEngine.OutlineContours(nextLayers.Single(layer => layer.Role.StartsWith("Profile", StringComparison.Ordinal)).Shapes);
        ApplyData(nextBoard, nextLayers, nextOutline, fills);
    }

    internal void SetLayout(CamInput input, PanelOptions options, CamBounds boards, CamBounds panel, IReadOnlyList<CamPoint> fiducials)
    {
        var nextBoard = ToRect(panel);
        var offsets = Enumerable.Range(0, options.Rows).SelectMany(row => Enumerable.Range(0, options.Columns)
            .Select(column => new CamPoint(column * options.PitchX(input.Bounds), row * options.PitchY(input.Bounds)))).ToArray();
        var fills = LayoutFillAreas(input.Bounds, boards, panel, options);
        var railContours = fills.Select(RectangleContour).ToArray();
        var nextLayers = new List<Layer>();
        var nextOutline = new List<IReadOnlyList<CamPoint>>();
        foreach (var file in input.Files)
        {
            var shapes = CamEngine.Geometry(file);
            var translated = offsets.SelectMany(offset => shapes.Select(shape => CamEngine.Translate(shape, offset.X, offset.Y))).ToList();
            if (file.Role == "Copper,L1,Top")
                translated.AddRange(fiducials.Select(point => new CamShape("Flash", [point], options.FiducialDiameter,
                    options.FiducialDiameter, true, true, true)));
            if (file.Role == "Soldermask,Top" && CamEngine.GerberAttribute(file.Text, "FilePolarity") == "Negative")
                translated.AddRange(fiducials.Select(point => new CamShape("Flash", [point], options.MaskDiameter, options.MaskDiameter, true, true)));
            if (file.Role.StartsWith("Profile", StringComparison.Ordinal))
            {
                // 보드 외곽이 서로 접해도 각 원본 경계는 독립된 폐곡선으로 유지한다.
                var contours = CamEngine.OutlineContours(shapes);
                nextOutline.AddRange(offsets.SelectMany(offset => contours.Select(contour => (IReadOnlyList<CamPoint>)contour
                    .Select(point => new CamPoint(point.X + offset.X, point.Y + offset.Y)).ToArray())));
                nextOutline.AddRange(railContours);
                translated.AddRange(railContours.SelectMany(contour => contour.Zip(contour.Skip(1),
                    (from, to) => new CamShape("Line", [from, to], .1m, .1m, true, true))));
            }
            nextLayers.Add(BuildLayer(file, translated, nextBoard));
        }
        ApplyData(nextBoard, nextLayers, nextOutline, fills);
    }

    private static IReadOnlyList<CamBounds> LayoutFillAreas(CamBounds source, CamBounds boards, CamBounds panel, PanelOptions options)
    {
        boards = CamEngine.RailLimit(boards, options);
        var areas = new List<CamBounds>
        {
            new(panel.MinX, panel.MinY, boards.MinX, panel.MaxY), new(boards.MaxX, panel.MinY, panel.MaxX, panel.MaxY),
            new(boards.MinX, panel.MinY, boards.MaxX, boards.MinY), new(boards.MinX, boards.MaxY, boards.MaxX, panel.MaxY)
        };
        if (options.FillGaps && options.Routing is null)
        {
            for (var row = 0; row < options.Rows; row++)
            {
                var y = source.MinY + row * options.PitchY(source);
                if (row + 1 < options.Rows && options.RowGap > 0)
                    areas.Add(new(boards.MinX, y + source.Height, boards.MaxX, y + options.PitchY(source)));
                if (options.ColumnGap > 0)
                    for (var column = 1; column < options.Columns; column++)
                    {
                        var x = source.MinX + column * options.PitchX(source);
                        areas.Add(new(x - options.ColumnGap, y, x, y + source.Height));
                    }
            }
        }
        return areas.Where(area => area.Width > 0 && area.Height > 0).ToArray();
    }

    private static Rect ToRect(CamBounds bounds) => new((double)bounds.MinX, -(double)bounds.MaxY,
        (double)bounds.Width, (double)bounds.Height);

    private static IReadOnlyList<CamPoint> RectangleContour(CamBounds bounds) =>
        [new(bounds.MinX, bounds.MinY), new(bounds.MaxX, bounds.MinY), new(bounds.MaxX, bounds.MaxY),
            new(bounds.MinX, bounds.MaxY), new(bounds.MinX, bounds.MinY)];

    private void ApplyData(Rect nextBoard, IReadOnlyList<Layer> nextLayers, IReadOnlyList<IReadOnlyList<CamPoint>> nextOutline,
        IReadOnlyList<CamBounds> fills)
    {
        var sameBoard = hasData && board == nextBoard;
        board = nextBoard;
        layers = nextLayers;
        outline = nextOutline;
        editableFiducials = [];
        editableBoards = [];
        selectedBoard = -1;
        SetEditableTabs(null, 0, false);
        ClearPlanDrag();
        guideAreas = fills.Select(ToRect).ToArray();
        var materialOutline = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = materialOutline.Open())
            foreach (var contour in outline)
            {
                context.BeginFigure(FromCam(contour[0]), isFilled: true, isClosed: true);
                context.PolyLineTo(contour.Skip(1).Select(FromCam).ToArray(), isStroked: true, isSmoothJoin: false);
            }
        materialOutline.Freeze();
        substrate = materialOutline;
        visible = nextLayers.Select(layer => layer.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        differences = [];
        hasData = board.Width > 0 && board.Height > 0;
        measureStart = null;
        measureEnd = null;
        measuringResult.Text = "";
        if (!sameBoard)
        {
            FitPlan();
            orbitZoom = 1;
        }
        InvalidateViews();
    }

    internal void SetEditableFiducials(IReadOnlyList<CamPoint> points)
    {
        editableFiducials = points;
        canvas.InvalidateVisual();
    }

    internal void SetEditableTabs(IReadOnlyList<MixedTab>? positions, decimal width, bool manual)
    {
        editableTabs = positions ?? [];
        tabHalfWidth = width / 2;
        selectedTab = -1;
        addTab.IsChecked = false;
        addTab.Visibility = deleteTab.Visibility = positions is null ? Visibility.Collapsed : Visibility.Visible;
        deleteTab.IsEnabled = false;
        resetTabs.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        canvas.InvalidateVisual();
    }

    internal void SetEditableBoards(IReadOnlyList<MixedBoard> boards, int selection)
    {
        editableBoards = boards;
        SelectBoard(selection);
    }

    internal void SelectBoard(int selection)
    {
        selectedBoard = selection;
        canvas.InvalidateVisual();
    }

    public void SetVisibleRoles(IEnumerable<string> roles)
    {
        visible = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        InvalidateViews();
    }

    internal void SetLayerColors(IReadOnlyDictionary<string, string> colors)
    {
        layerColors = colors;
        if (!hasData)
            return;
        layers = layers.Select(layer =>
        {
            var brush = LayerBrush(layer.Role);
            return layer with { Color = brush, PlanDrawing = MakePlanDrawing(layer.Role, layer.Elements, brush, board) };
        }).ToArray();
        InvalidateViews();
    }

    public void SetThickness(decimal thickness)
    {
        if (thickness <= 0)
            throw new ArgumentOutOfRangeException(nameof(thickness));
        boardThickness = (double)thickness;
        thicknessField.Text = thickness.ToString("0.###", CultureInfo.InvariantCulture);
        thicknessError.Text = "";
        InvalidateViews();
    }

    public void Focus(CamPoint point)
    {
        if (!hasData)
            return;
        tabs.SelectedIndex = 0;
        zoomFactor = Math.Max(zoomFactor, 4);
        pan = (Center(board) - FromCam(point)) * PlanScale;
        canvas.InvalidateVisual();
    }

    public void ShowComparison(IReadOnlyList<CamShape> missing, IReadOnlyList<CamShape> added)
    {
        var missingBrush = BrushOf(255, 101, 134);
        var addedBrush = BrushOf(100, 242, 226);
        differences = missing.Select(shape => (ShapeGeometry(shape), missingBrush))
            .Concat(added.Select(shape => (ShapeGeometry(shape), addedBrush))).ToArray();
        if (differences.Count > 0)
            tabs.SelectedIndex = 0;
        canvas.InvalidateVisual();
    }

    private static UIElement CreatePage(UIElement view, params UIElement[] tools)
    {
        var page = new DockPanel { Background = CanvasColor };
        var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
        foreach (var tool in tools)
            bar.Children.Add(tool);
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        page.Children.Add(view);
        return page;
    }

    private static Button Button(string caption, Action action)
    {
        var button = new Button { Content = caption, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(7, 2, 7, 2) };
        button.Click += (_, _) => action();
        return button;
    }

    private Layer BuildLayer(CamFile file, IReadOnlyList<CamShape> shapes, Rect extent)
    {
        var elements = shapes.Select(shape => (Shape: shape, Geometry: ShapeGeometry(shape))).ToArray();
        var brush = LayerBrush(file.Role);
        return new Layer(file.Role, file.IsDrill, shapes, elements, brush,
            CamEngine.GerberAttribute(file.Text, "FilePolarity") == "Positive", MakePlanDrawing(file.Role, elements, brush, extent));
    }

    private Brush LayerBrush(string role) => layerColors.TryGetValue(role, out var hex) && LayerStyleEditor.TryColor(hex, out var color)
        ? BrushOf(color.R, color.G, color.B) : DefaultLayerBrush(role);

    internal static string DefaultLayerHex(string role)
    {
        var color = ((SolidColorBrush)DefaultLayerBrush(role)).Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static Brush DefaultLayerBrush(string role) => role.Split(',')[0] switch
        {
            "Copper" => role.EndsWith(",Bot", StringComparison.OrdinalIgnoreCase) ? BrushOf(104, 171, 236) : CopperColor,
            "Soldermask" => BrushOf(104, 213, 142),
            "Legend" => BrushOf(236, 233, 213),
            "Paste" => BrushOf(205, 205, 219),
            "Profile" => ProfileColor,
            "Vcut" => CutColor,
            "Plated" or "NonPlated" => BrushOf(133, 226, 230),
            _ => ProfileColor
        };

    private static DrawingGroup MakePlanDrawing(string role, (CamShape Shape, Geometry Geometry)[] elements, Brush color, Rect extent)
    {
        var group = new DrawingGroup();
        using (var drawing = group.Open())
            DrawLayer(drawing, elements, color, extent);
        group.Opacity = role.StartsWith("Soldermask,", StringComparison.OrdinalIgnoreCase) ? 0.58 : 0.92;
        group.Freeze();
        return group;
    }

    private static Geometry ShapeGeometry(CamShape shape)
    {
        Geometry result;
        if (shape.Contours is { Count: > 0 })
        {
            var contours = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var writer = contours.Open())
            {
                foreach (var contour in shape.Contours.Where(contour => contour.Count > 2))
                {
                    writer.BeginFigure(FromCam(contour[0]), true, true);
                    writer.PolyLineTo(contour.Skip(1).Select(FromCam).ToList(), true, false);
                }
            }
            result = contours;
        }
        else if (shape.Points.Count == 0)
        {
            return Geometry.Empty;
        }
        else if (shape.Kind is "Flash" or "Drill")
        {
            var center = FromCam(shape.Points[0]);
            var width = shape.Width > 0 ? (double)shape.Width : 0.01;
            var height = shape.Height > 0 ? (double)shape.Height : width;
            result = shape.IsCircular || shape.Kind == "Drill"
                ? new EllipseGeometry(center, width / 2, shape.Kind == "Drill" ? width / 2 : height / 2)
                : new RectangleGeometry(new Rect(center.X - width / 2, center.Y - height / 2, width, height));
        }
        else
        {
            var path = new StreamGeometry();
            using (var writer = path.Open())
            {
                var region = shape.Kind == "Region";
                writer.BeginFigure(FromCam(shape.Points[0]), region, region);
                writer.PolyLineTo(shape.Points.Skip(1).Select(FromCam).ToList(), true, false);
            }
            if (shape.Kind == "Region")
            {
                result = path;
            }
            else
            {
                var radiusWidth = shape.Width > 0 ? (double)shape.Width : 0.02;
                if (shape.Points.All(point => point == shape.Points[0]))
                {
                    result = new EllipseGeometry(FromCam(shape.Points[0]), radiusWidth / 2, radiusWidth / 2);
                }
                else
                {
                    var pen = new Pen(Brushes.White, radiusWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    result = path.GetWidenedPathGeometry(pen, 0.0001, ToleranceType.Absolute);
                }
            }
        }
        result.Freeze();
        return result;
    }

    private void InvalidateViews()
    {
        sceneDirty = true;
        canvas.InvalidateVisual();
        RefreshSceneIfVisible();
    }

    private double PlanScale => hasData
        ? Math.Max(0.0001, Math.Min((canvas.ActualWidth - 56) / board.Width, (canvas.ActualHeight - 64) / board.Height)) * zoomFactor
        : 1;

    private Point PlanCenter => new(canvas.ActualWidth / 2 + pan.X, canvas.ActualHeight / 2 + pan.Y);

    private void FitPlan()
    {
        zoomFactor = 1;
        pan = default;
        canvas.InvalidateVisual();
    }

    private void DrawPlan(DrawingContext drawing)
    {
        drawing.DrawRectangle(CanvasColor, null, new Rect(canvas.RenderSize));
        if (!hasData)
        {
            DrawText(drawing, "Gerber ZIP 또는 폴더를 열어 패널을 확인하세요.", new Point(24, 27));
            return;
        }

        var view = new Matrix(PlanScale, 0, 0, PlanScale,
            PlanCenter.X - Center(board).X * PlanScale, PlanCenter.Y - Center(board).Y * PlanScale);
        drawing.PushTransform(new MatrixTransform(view));
        drawing.DrawGeometry(BoardColor, null, substrate);
        foreach (var area in guideAreas)
            drawing.DrawRectangle(GuideColor, null, area);
        foreach (var layer in VisibleLayers().OrderBy(LayerOrder))
            drawing.DrawDrawing(layer.PlanDrawing);
        foreach (var (shape, brush) in differences)
            drawing.DrawGeometry(brush, new Pen(brush, 1.5 / PlanScale), shape);
        drawing.Pop();

        for (var index = 0; index < editableTabs.Count; index++)
        {
            var tab = index == draggedTab ? tabTarget ?? editableTabs[index] : editableTabs[index];
            var stroke = new Pen(index == selectedTab ? Brushes.White : CutColor, index == selectedTab ? 2 : 1);
            drawing.DrawRectangle(null, stroke, Rect.Transform(ToRect(tab.Bounds(tabHalfWidth)), view));
        }

        if (selectedBoard >= 0 && selectedBoard < editableBoards.Count)
        {
            var selected = editableBoards[selectedBoard];
            var bounds = ToRect((dragTarget is { } target && draggedBoard == selectedBoard ? selected with { Origin = target } : selected).Bounds);
            var outlinePen = new Pen(FiducialColor, 1.5) { DashStyle = DashStyles.Dash };
            drawing.DrawRectangle(null, outlinePen, Rect.Transform(bounds, view));
        }

        if (visible.Contains("Copper,L1,Top"))
            for (var index = 0; index < editableFiducials.Count; index++)
            {
                var point = index == draggedFiducial ? dragTarget ?? editableFiducials[index] : editableFiducials[index];
                var screen = view.Transform(FromCam(point));
                drawing.DrawEllipse(null, new Pen(FiducialColor, 1.6), screen, 6, 6);
                if (index == draggedFiducial)
                    DrawText(drawing, FormattableString.Invariant($"{point.X:0.#####}, {point.Y:0.#####} mm"), screen + new Vector(10, 9));
            }

        DrawText(drawing, FormattableString.Invariant($"{board.Width:0.###} × {board.Height:0.###} mm"), new Point(12, Math.Max(2, canvas.ActualHeight - 25)));
        if (measureStart is { } start && measureEnd is { } end)
        {
            var from = view.Transform(start);
            var to = view.Transform(end);
            var stroke = new Pen(Brushes.White, 1.4);
            drawing.DrawLine(stroke, from, to);
            drawing.DrawEllipse(Brushes.White, null, from, 3, 3);
            drawing.DrawEllipse(Brushes.White, null, to, 3, 3);
        }
    }

    private void DrawText(DrawingContext drawing, string caption, Point point)
    {
        var text = new FormattedText(caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Malgun Gothic"), 13, TextColor, VisualTreeHelper.GetDpi(canvas).PixelsPerDip);
        drawing.DrawText(text, point);
    }

    private IEnumerable<Layer> VisibleLayers() => layers.Where(layer => visible.Contains(layer.Role));

    private static int LayerOrder(Layer layer) => layer.Role.Split(',')[0] switch
    {
        "Copper" => 0,
        "Soldermask" => 1,
        "Paste" => 2,
        "Legend" => 3,
        "Profile" => 4,
        "Vcut" => 5,
        _ => 6
    };

    private static void DrawLayer(DrawingContext drawing, (CamShape Shape, Geometry Geometry)[] elements, Brush brush, Rect extent)
    {
        // 하나의 파일에서 나중에 그리는 clear 객체만 앞의 dark 객체에 영향을 준다.
        var pushes = 0;
        for (var index = elements.Length - 1; index >= 0; index--)
        {
            var (shape, geometry) = elements[index];
            if (shape.Dark)
                drawing.DrawGeometry(brush, null, geometry);
            else
            {
                var end = index + 1;
                while (index > 0 && !elements[index - 1].Shape.Dark)
                    index--;
                var clip = Combine(new RectangleGeometry(extent), MergeRange(elements, index, end), GeometryCombineMode.Exclude);
                drawing.PushClip(clip);
                pushes++;
            }
        }
        while (pushes-- > 0)
            drawing.Pop();
    }

    private void ZoomPlan(object sender, MouseWheelEventArgs e)
    {
        if (!hasData)
            return;
        var cursor = e.GetPosition(canvas);
        var previous = PlanScale;
        zoomFactor = Math.Clamp(zoomFactor * Math.Pow(1.16, e.Delta / 120.0), 0.4, 75);
        pan += (cursor - PlanCenter) * (1 - PlanScale / previous);
        canvas.InvalidateVisual();
        e.Handled = true;
    }

    private void BeginPlanGesture(object sender, MouseButtonEventArgs e)
    {
        if (!hasData)
            return;
        var cursor = e.GetPosition(canvas);
        if (measure.IsChecked == true)
        {
            var point = Center(board) + (cursor - PlanCenter) / PlanScale;
            if (measureStart is null || measureEnd is not null)
            {
                measureStart = point;
                measureEnd = null;
                measuringResult.Text = "두 번째 점 선택";
            }
            else
            {
                measureEnd = point;
                measuringResult.Text = FormattableString.Invariant($"{(point - measureStart.Value).Length:0.###} mm");
            }
            canvas.InvalidateVisual();
            return;
        }
        if (addTab.IsChecked == true)
        {
            var point = Center(board) + (cursor - PlanCenter) / PlanScale;
            addTab.IsChecked = false;
            TabsEdited?.Invoke(editableTabs, new CamPoint(decimal.Round((decimal)point.X, 6), decimal.Round(-(decimal)point.Y, 6)));
            return;
        }
        dragStart = cursor;
        dragOffset = pan;
        selectedTab = draggedTab = FindTab(cursor);
        deleteTab.IsEnabled = selectedTab >= 0;
        draggedFiducial = draggedTab < 0 ? FindFiducial(cursor) : -1;
        dragTarget = draggedFiducial >= 0 ? editableFiducials[draggedFiducial] : null;
        if (draggedTab < 0 && draggedFiducial < 0 && editableBoards.Count > 0)
        {
            draggedBoard = FindBoard(cursor);
            BoardSelected?.Invoke(draggedBoard);
        }
        canvas.CaptureMouse();
        canvas.InvalidateVisual();
    }

    private void MovePlanGesture(object sender, MouseEventArgs e)
    {
        var cursor = e.GetPosition(canvas);
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            canvas.Cursor = addTab.IsChecked == true ? Cursors.Cross : measure.IsChecked != true &&
                (FindTab(cursor) >= 0 || FindFiducial(cursor) >= 0 || FindBoard(cursor) >= 0) ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }
        if (draggedTab >= 0)
            tabTarget = MoveTab(editableTabs[draggedTab], cursor - start, PlanScale);
        else if (draggedFiducial >= 0)
            dragTarget = SnapFiducial(editableFiducials[draggedFiducial], cursor - start, PlanScale);
        else if (draggedBoard >= 0)
            dragTarget = SnapBoard(editableBoards[draggedBoard].Origin, cursor - start, PlanScale);
        else
            pan = dragOffset + (cursor - start);
        canvas.InvalidateVisual();
    }

    private int FindTab(Point cursor)
    {
        var position = Center(board) + (cursor - PlanCenter) / PlanScale;
        for (var index = 0; index < editableTabs.Count; index++)
        {
            var hit = ToRect(editableTabs[index].Bounds(tabHalfWidth));
            hit.Inflate(3 / PlanScale, 3 / PlanScale);
            if (hit.Contains(position))
                return index;
        }
        return -1;
    }

    internal static MixedTab MoveTab(MixedTab tab, Vector delta, double scale) => tab with
    {
        Center = tab.Center + (decimal)Math.Round((tab.Vertical ? -delta.Y : delta.X) / scale, 1, MidpointRounding.AwayFromZero)
    };

    private int FindFiducial(Point cursor)
    {
        if (!hasData || !visible.Contains("Copper,L1,Top"))
            return -1;
        for (var index = 0; index < editableFiducials.Count; index++)
            if ((cursor - (PlanCenter + (FromCam(editableFiducials[index]) - Center(board)) * PlanScale)).Length <= 9)
                return index;
        return -1;
    }

    internal static CamPoint SnapFiducial(CamPoint original, Vector delta, double scale) => new(
        original.X + (decimal)Math.Round(delta.X / scale, MidpointRounding.AwayFromZero),
        original.Y - (decimal)Math.Round(delta.Y / scale, MidpointRounding.AwayFromZero));

    private int FindBoard(Point cursor)
    {
        if (!hasData || editableBoards.Count == 0)
            return -1;
        var position = Center(board) + (cursor - PlanCenter) / PlanScale;
        for (var index = editableBoards.Count - 1; index >= 0; index--)
            if (ToRect(editableBoards[index].Bounds).Contains(position))
                return index;
        return -1;
    }

    internal static CamPoint SnapBoard(CamPoint original, Vector delta, double scale) => new(
        original.X + (decimal)Math.Round(delta.X / scale, 1, MidpointRounding.AwayFromZero),
        original.Y - (decimal)Math.Round(delta.Y / scale, 1, MidpointRounding.AwayFromZero));

    private void EndPlanGesture(object sender, MouseButtonEventArgs e)
    {
        CamPoint[]? moved = null;
        MixedTab[]? movedTabs = null;
        var boardIndex = draggedBoard;
        CamPoint? boardTarget = null;
        if (draggedTab >= 0 && dragStart is { } tabStart)
        {
            var original = editableTabs[draggedTab];
            var target = MoveTab(original, e.GetPosition(canvas) - tabStart, PlanScale);
            if (target != original)
            {
                movedTabs = editableTabs.ToArray();
                movedTabs[draggedTab] = target;
            }
        }
        else if (draggedFiducial >= 0 && dragStart is { } start)
        {
            var target = SnapFiducial(editableFiducials[draggedFiducial], e.GetPosition(canvas) - start, PlanScale);
            if (target != editableFiducials[draggedFiducial])
            {
                moved = editableFiducials.ToArray();
                moved[draggedFiducial] = target;
            }
        }
        else if (boardIndex >= 0 && dragStart is { } boardStart)
        {
            var origin = editableBoards[boardIndex].Origin;
            var target = SnapBoard(origin, e.GetPosition(canvas) - boardStart, PlanScale);
            if (target != origin)
                boardTarget = target;
        }
        ClearPlanDrag();
        canvas.ReleaseMouseCapture();
        if (movedTabs is not null)
            TabsEdited?.Invoke(movedTabs, null);
        if (moved is not null)
            FiducialsMoved?.Invoke(moved);
        if (boardTarget is { } next)
            BoardMoved?.Invoke(boardIndex, next);
    }

    private void ClearPlanDrag()
    {
        dragStart = null;
        draggedFiducial = -1;
        draggedBoard = -1;
        draggedTab = -1;
        tabTarget = null;
        dragTarget = null;
        canvas.InvalidateVisual();
    }

    private void ClearMeasurement()
    {
        measureStart = null;
        measureEnd = null;
        measuringResult.Text = "";
        canvas.InvalidateVisual();
    }

    private void CommitThickness()
    {
        if (!decimal.TryParse(thicknessField.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            thicknessError.Text = "0보다 큰 두께를 입력하세요.";
            return;
        }
        if ((double)value != boardThickness)
            SetThickness(value);
        thicknessError.Text = "";
    }

    private void RefreshSceneIfVisible()
    {
        if (!sceneDirty || tabs.SelectedIndex != 1)
            return;
        var nextScene = new Model3DGroup();
        nextScene.Children.Add(new AmbientLight(Color.FromRgb(112, 112, 112)));
        nextScene.Children.Add(new DirectionalLight(Color.FromRgb(190, 190, 190), new Vector3D(-0.2, 0.4, -1)));
        nextScene.Children.Add(new DirectionalLight(Color.FromRgb(145, 145, 145), new Vector3D(0.5, -0.4, 1)));
        if (hasData)
        {
            var holes = VisibleLayers().Where(layer => layer.IsDrill).SelectMany(layer => layer.Shapes)
                .Where(shape => ThroughHole(shape) && shape.Points.All(point => substrate.FillContains(FromCam(point))))
                .DistinctBy(shape => (shape.Points[0], shape.Points[^1], shape.Width)).ToArray();
            AddFace(nextScene, substrate, holes, top: true);
            AddFace(nextScene, substrate, holes, top: false);
            AddWalls(nextScene, holes);
        }
        scene.Content = nextScene;
        sceneDirty = false;
        UpdateCamera();
    }

    private void AddFace(Model3DGroup model, Geometry substrate, IReadOnlyList<CamShape> holes, bool top)
    {
        const double targetPixels = 1536;
        var pixelScale = targetPixels / Math.Max(board.Width, board.Height);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(board.Width * pixelScale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(board.Height * pixelScale));
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
        {
            drawing.PushTransform(new MatrixTransform(pixelWidth / board.Width, 0, 0, pixelHeight / board.Height,
                -board.X * pixelWidth / board.Width, -board.Y * pixelHeight / board.Height));
            var holeGroup = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var hole in holes)
                holeGroup.Children.Add(ShapeGeometry(hole));
            drawing.PushClip(Combine(substrate, holeGroup, GeometryCombineMode.Exclude));
            var side = top ? ",Top" : ",Bot";
            var sideLayers = VisibleLayers().Where(layer => layer.Role.EndsWith(side, StringComparison.OrdinalIgnoreCase)).ToArray();
            var mask = sideLayers.FirstOrDefault(layer => layer.Role.StartsWith("Soldermask,", StringComparison.OrdinalIgnoreCase));
            drawing.DrawRectangle(mask is not null && layerColors.ContainsKey(mask.Role) ? mask.Color : SurfaceColor, null, board);
            foreach (var area in guideAreas)
                drawing.DrawRectangle(GuideColor, null, area);
            if (mask is not null)
            {
                var maskGeometry = LayerArea(mask);
                var openings = mask.Positive ? Combine(new RectangleGeometry(board), maskGeometry, GeometryCombineMode.Exclude) : maskGeometry;
                drawing.PushClip(openings);
            }
            foreach (var copper in sideLayers.Where(layer => layer.Role.StartsWith("Copper,", StringComparison.OrdinalIgnoreCase)))
                DrawLayer(drawing, copper.Elements, layerColors.ContainsKey(copper.Role) ? copper.Color : CopperColor, board);
            if (mask is not null)
                drawing.Pop();
            foreach (var legend in sideLayers.Where(layer => layer.Role.StartsWith("Legend,", StringComparison.OrdinalIgnoreCase)))
                DrawLayer(drawing, legend.Elements, legend.Color, board);
            foreach (var cut in VisibleLayers().Where(layer => layer.Role.StartsWith("Vcut", StringComparison.OrdinalIgnoreCase)))
                foreach (var shape in cut.Shapes.Where(shape => shape.Points.Count > 1))
                {
                    var from = FromCam(shape.Points[0]);
                    var to = FromCam(shape.Points[^1]);
                    drawing.DrawLine(new Pen(CanvasColor, 5 / pixelScale), from, to);
                    drawing.DrawLine(new Pen(cut.Color, Math.Max(0.15, 3 / pixelScale)), from, to);
                }
            foreach (var copper in sideLayers.Where(layer => layer.Role.StartsWith("Copper,", StringComparison.OrdinalIgnoreCase)))
                foreach (var mark in copper.Shapes.Where(shape => shape.IsFiducial && shape.Kind == "Flash"))
                {
                    var center = FromCam(mark.Points[0]);
                    var radius = (double)mark.Width / 2;
                    drawing.DrawEllipse(FiducialColor, new Pen(CanvasColor, 1 / pixelScale), center, radius, radius);
                    var ring = Math.Max(radius + 2 / pixelScale, 4 / pixelScale);
                    drawing.DrawEllipse(null, new Pen(FiducialColor, 1.5 / pixelScale), center, ring, ring);
                }
            drawing.Pop();
            drawing.Pop();
        }
        var image = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(surface);
        image.Freeze();
        var material = new DiffuseMaterial(new ImageBrush(image) { Stretch = Stretch.Fill });
        var z = top ? boardThickness / 2 : -boardThickness / 2;
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { ToModel(board.TopLeft, z), ToModel(board.BottomLeft, z), ToModel(board.BottomRight, z), ToModel(board.TopRight, z) },
            TextureCoordinates = new PointCollection { new(0, 0), new(0, 1), new(1, 1), new(1, 0) },
            TriangleIndices = top ? new Int32Collection { 0, 1, 2, 0, 2, 3 } : new Int32Collection { 0, 2, 1, 0, 3, 2 }
        };
        model.Children.Add(new GeometryModel3D(mesh, material));
    }

    private static bool ThroughHole(CamShape shape) => (shape.Kind == "Drill" && shape.Points.Count == 1 ||
        shape.Kind == "Slot" && shape.Points.Count == 2) && shape.Width > 0;

    private void AddWalls(Model3DGroup model, IReadOnlyList<CamShape> holes)
    {
        var edgeMaterial = new DiffuseMaterial(BrushOf(136, 119, 77));
        var edges = new MeshGeometry3D();
        foreach (var contour in outline)
            for (var index = 1; index < contour.Count; index++)
                AddWall(edges, FromCam(contour[index - 1]), FromCam(contour[index]));
        model.Children.Add(new GeometryModel3D(edges, edgeMaterial) { BackMaterial = edgeMaterial });

        var boreMaterial = new DiffuseMaterial(BrushOf(76, 62, 45));
        foreach (var batch in holes.Chunk(128))
        {
            var bores = new MeshGeometry3D();
            foreach (var hole in batch)
            {
                if (hole.Kind == "Slot" && hole.Contours is { Count: > 0 } contours)
                {
                    foreach (var contour in contours)
                        for (var index = 1; index < contour.Count; index++)
                            AddWall(bores, FromCam(contour[index - 1]), FromCam(contour[index]));
                    continue;
                }
                var center = FromCam(hole.Points[0]);
                var radius = (double)hole.Width / 2;
                const int sides = 24;
                for (var index = 0; index < sides; index++)
                {
                    var from = index * Math.Tau / sides;
                    var to = (index + 1) * Math.Tau / sides;
                    AddWall(bores, center + new Vector(radius * Math.Cos(from), radius * Math.Sin(from)),
                        center + new Vector(radius * Math.Cos(to), radius * Math.Sin(to)));
                }
            }
            model.Children.Add(new GeometryModel3D(bores, boreMaterial) { BackMaterial = boreMaterial });
        }
    }

    private void AddWall(MeshGeometry3D mesh, Point from, Point to)
    {
        var index = mesh.Positions.Count;
        var z = boardThickness / 2;
        mesh.Positions.Add(ToModel(from, z));
        mesh.Positions.Add(ToModel(from, -z));
        mesh.Positions.Add(ToModel(to, -z));
        mesh.Positions.Add(ToModel(to, z));
        mesh.TriangleIndices.Add(index);
        mesh.TriangleIndices.Add(index + 1);
        mesh.TriangleIndices.Add(index + 2);
        mesh.TriangleIndices.Add(index);
        mesh.TriangleIndices.Add(index + 2);
        mesh.TriangleIndices.Add(index + 3);
    }

    private Point3D ToModel(Point point, double z)
    {
        var center = Center(board);
        return new Point3D(point.X - center.X, center.Y - point.Y, z);
    }

    private static Geometry LayerArea(Layer layer)
    {
        // 균형 있게 병합해 마스크의 많은 독립 개구를 한 번씩 누적하는 비용을 줄인다.
        var area = Geometry.Empty;
        var index = 0;
        while (index < layer.Elements.Length)
        {
            var from = index;
            var dark = layer.Elements[from].Shape.Dark;
            while (index < layer.Elements.Length && layer.Elements[index].Shape.Dark == dark)
                index++;
            var run = MergeRange(layer.Elements, from, index);
            area = Combine(area, run, dark ? GeometryCombineMode.Union : GeometryCombineMode.Exclude);
        }
        return area;
    }

    private static Geometry MergeRange((CamShape Shape, Geometry Geometry)[] elements, int start, int end)
    {
        if (end - start == 1)
            return elements[start].Geometry;
        var middle = start + (end - start) / 2;
        return Combine(MergeRange(elements, start, middle), MergeRange(elements, middle, end), GeometryCombineMode.Union);
    }

    private static Geometry Combine(Geometry first, Geometry second, GeometryCombineMode mode) =>
        Geometry.Combine(first, second, mode, null, 0.0001, ToleranceType.Absolute);

    private void SetOrbit(double angle, double tilt)
    {
        azimuth = angle;
        elevation = tilt;
        orbitZoom = 1;
        UpdateCamera();
    }

    private void RotateScene(object sender, MouseEventArgs e)
    {
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;
        var point = e.GetPosition(viewport);
        var delta = point - start;
        azimuth -= delta.X * 0.6;
        elevation = Math.Clamp(elevation + delta.Y * 0.6, -89.9, 89.9);
        dragStart = point;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if (!hasData)
            return;
        var angle = azimuth * Math.PI / 180;
        var tilt = elevation * Math.PI / 180;
        var distance = Math.Max(board.Width, board.Height) * 3 + boardThickness;
        var location = new Vector3D(Math.Cos(tilt) * Math.Cos(angle), Math.Cos(tilt) * Math.Sin(angle), Math.Sin(tilt)) * distance;
        camera.Position = new Point3D(location.X, location.Y, location.Z);
        camera.LookDirection = -location;
        camera.UpDirection = new Vector3D(0, 0, 1);
        var ratio = viewport.ActualHeight > 0 ? Math.Max(1, viewport.ActualWidth / viewport.ActualHeight) : 1;
        camera.Width = Math.Sqrt(board.Width * board.Width + board.Height * board.Height) * 1.12 * ratio / orbitZoom;
    }

    private static Point FromCam(CamPoint point) => new((double)point.X, -(double)point.Y);

    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static Brush BrushOf(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record Layer(string Role, bool IsDrill, IReadOnlyList<CamShape> Shapes,
        (CamShape Shape, Geometry Geometry)[] Elements, Brush Color, bool Positive, DrawingGroup PlanDrawing);

    private sealed class CanvasSurface(Action<DrawingContext> render) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) => render(drawingContext);
    }
}
