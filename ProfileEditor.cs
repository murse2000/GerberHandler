using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace GerberHandler;

internal sealed record Profile(string Name, string Source, DateOnly Date, decimal MinWidth, decimal MaxWidth,
    decimal MinHeight, decimal MaxHeight, decimal MinRail, decimal Clearance, decimal FiducialDiameter,
    decimal MaskDiameter, decimal ToolingDiameter, decimal MinCutSpacing = 0)
{
    internal static Profile Reference { get; } = new("Eurocircuits 조립 · 공개 조건 참고", "Eurocircuits 조립 패널·동박 여유 가이드 참고",
        new DateOnly(2026, 11, 26), 36, 326, 46, 426, 5, .45m, 1.5m, 3, 3, 5);

    public IEnumerable<PanelFinding> Validate(CamPanel panel, PanelOptions options)
    {
        if (options.Routing is null && options.HasGap && MinCutSpacing <= 0)
            yield return new("빈 PCB 스트립을 제조하려면 프리셋에 최소 V-cut 간격을 입력하세요.", null);
        if (MinCutSpacing > 0)
        {
            foreach (var axis in panel.Cuts.GroupBy(cut => cut.From.X == cut.To.X))
            {
                var positions = axis.Select(cut => axis.Key ? cut.From.X : cut.From.Y).Order().ToArray();
                for (var index = 1; index < positions.Length; index++)
                {
                    var distance = positions[index] - positions[index - 1];
                    if (distance < MinCutSpacing)
                        yield return new($"V-cut 간격 {MainWindow.Number(distance)} mm · 최솟값 {MainWindow.Number(MinCutSpacing)} mm",
                            axis.Key ? new CamPoint(positions[index], panel.Bounds.MinY) : new CamPoint(panel.Bounds.MinX, positions[index]));
                }
            }
        }
        if (panel.Bounds.Width < MinWidth || panel.Bounds.Width > MaxWidth)
            yield return new($"패널 가로 {MainWindow.Number(panel.Bounds.Width)} mm · 허용 {MainWindow.Number(MinWidth)}–{MainWindow.Number(MaxWidth)} mm", new CamPoint(panel.Bounds.MaxX, panel.Bounds.MinY));
        if (panel.Bounds.Height < MinHeight || panel.Bounds.Height > MaxHeight)
            yield return new($"패널 세로 {MainWindow.Number(panel.Bounds.Height)} mm · 허용 {MainWindow.Number(MinHeight)}–{MainWindow.Number(MaxHeight)} mm", new CamPoint(panel.Bounds.MinX, panel.Bounds.MaxY));

        (string Side, decimal Width, CamPoint Point)[] rails =
        [
            ("왼쪽", options.RailLeft, new(panel.Bounds.MinX, (panel.Bounds.MinY + panel.Bounds.MaxY) / 2)),
            ("오른쪽", options.RailRight, new(panel.Bounds.MaxX, (panel.Bounds.MinY + panel.Bounds.MaxY) / 2)),
            ("아래", options.RailBottom, new((panel.Bounds.MinX + panel.Bounds.MaxX) / 2, panel.Bounds.MinY)),
            ("위", options.RailTop, new((panel.Bounds.MinX + panel.Bounds.MaxX) / 2, panel.Bounds.MaxY))
        ];
        foreach (var rail in rails.Where(rail => rail.Width > 0 && rail.Width < MinRail))
            yield return new($"{rail.Side} 레일 {MainWindow.Number(rail.Width)} mm · 최솟값 {MainWindow.Number(MinRail)} mm", rail.Point);

        if (options.Fiducials && (options.FiducialDiameter != FiducialDiameter || options.MaskDiameter != MaskDiameter))
            yield return new($"프리셋 피두셜 지름 {MainWindow.Number(FiducialDiameter)} mm · 마스크 개구 {MainWindow.Number(MaskDiameter)} mm", panel.Fiducials.FirstOrDefault());
        if (options.Tooling && options.ToolingDiameter != ToolingDiameter)
            yield return new($"프리셋 고정 홀 지름 {MainWindow.Number(ToolingDiameter)} mm", panel.Tooling.FirstOrDefault());
    }
}

internal static class ProfileEditor
{
    public static Profile? Show(Window owner, string currentFiducial, string currentMask, string currentTooling)
    {
        var form = new StackPanel { Margin = new Thickness(16) };
        var dialog = new Window
        {
            Title = "제조·조립 프리셋 등록",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height,
            Width = 490,
            Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        var shortcuts = new WrapPanel();
        var referenceButton = new Button { Content = "공개 조건 불러오기" };
        var marksButton = new Button { Content = "현재 마크 치수 사용" };
        shortcuts.Children.Add(referenceButton);
        shortcuts.Children.Add(marksButton);
        form.Children.Add(shortcuts);
        form.Children.Add(new TextBlock { Text = "공개 조건은 Eurocircuits 참고값입니다. 주문할 업체의 규격을 확인해 저장하세요.", TextWrapping = TextWrapping.Wrap });
        var name = AddField("프리셋 이름");
        var source = AddField("확인한 제조사 문서 또는 사내 기준");
        var minWidth = AddField("최소 패널 가로 (mm)");
        var maxWidth = AddField("최대 패널 가로 (mm)");
        var minHeight = AddField("최소 패널 세로 (mm)");
        var maxHeight = AddField("최대 패널 세로 (mm)");
        var rail = AddField("최소 레일 폭 (mm)");
        var clearance = AddField("V-cut 동박·홀 여유 / 타공과 보드 사이 여유 (mm)");
        var cutSpacing = AddField("최소 V-cut 간격 (mm, 빈 PCB 스트립 제조 시 필요)");
        var fiducial = AddField("피두셜 동박 지름 (mm)");
        var mask = AddField("피두셜 마스크 개구 지름 (mm)");
        var tooling = AddField("고정 홀 지름 (mm)");
        FillReference();
        referenceButton.Click += (_, _) => FillReference();
        marksButton.Click += (_, _) =>
        {
            fiducial.Text = currentFiducial;
            mask.Text = currentMask;
            tooling.Text = currentTooling;
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkRed };
        form.Children.Add(error);
        var save = new Button { Content = "확인한 조건 저장", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        form.Children.Add(save);
        Profile? result = null;

        save.Click += (_, _) =>
        {
            try
            {
                var profile = new Profile(name.Text.Trim(), source.Text.Trim(), DateOnly.FromDateTime(DateTime.Today),
                    Value(minWidth), Value(maxWidth), Value(minHeight), Value(maxHeight), Value(rail), Value(clearance),
                    Value(fiducial), Value(mask), Value(tooling), string.IsNullOrWhiteSpace(cutSpacing.Text) ? 0 : Value(cutSpacing));
                if (profile.Name.Length == 0 || profile.Source.Length == 0 || profile.MinWidth <= 0 || profile.MinHeight <= 0 ||
                    profile.MaxWidth < profile.MinWidth || profile.MaxHeight < profile.MinHeight || profile.MinRail < 0 ||
                    profile.Clearance <= 0 || profile.FiducialDiameter <= 0 || profile.MaskDiameter <= profile.FiducialDiameter ||
                    profile.ToolingDiameter <= 0 || profile.MinCutSpacing < 0)
                    throw new InvalidDataException("이름, 기준 출처, 최소·최대 치수, 절단 여유와 홀·마크 지름을 확인하세요.");
                result = profile;
                dialog.DialogResult = true;
            }
            catch (InvalidDataException exception)
            {
                error.Text = exception.Message;
            }
        };

        dialog.ShowDialog();
        return result;

        void FillReference()
        {
            var profile = Profile.Reference;
            name.Text = "내 제조·조립 조건";
            source.Text = profile.Source;
            minWidth.Text = MainWindow.Number(profile.MinWidth);
            maxWidth.Text = MainWindow.Number(profile.MaxWidth);
            minHeight.Text = MainWindow.Number(profile.MinHeight);
            maxHeight.Text = MainWindow.Number(profile.MaxHeight);
            rail.Text = MainWindow.Number(profile.MinRail);
            clearance.Text = MainWindow.Number(profile.Clearance);
            cutSpacing.Text = MainWindow.Number(profile.MinCutSpacing);
            fiducial.Text = MainWindow.Number(profile.FiducialDiameter);
            mask.Text = MainWindow.Number(profile.MaskDiameter);
            tooling.Text = MainWindow.Number(profile.ToolingDiameter);
        }

        TextBox AddField(string label)
        {
            form.Children.Add(new TextBlock { Text = label });
            var input = new TextBox();
            form.Children.Add(input);
            return input;
        }

        static decimal Value(TextBox input) => MainWindow.ReadNumber(input.Text);
    }
}
