using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Velopack;
using Velopack.Sources;

namespace GerberHandler;

internal static class UpdateService
{
    private const string RepositoryUrl = "https://github.com/murse2000/GerberHandler";
    private static bool checking;

    internal static async Task CheckAsync(Window owner, Func<bool> confirmRestart, bool manual = false)
    {
        if (checking)
            return;

        checking = true;
        bool approved = false;
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            if (!manager.IsInstalled)
            {
                if (manual)
                    MessageBox.Show(owner, "GitHub 릴리스의 설치 프로그램으로 설치한 앱에서 업데이트를 사용할 수 있습니다.", "업데이트 확인");
                return;
            }

            UpdateInfo? update = await manager.CheckForUpdatesAsync();
            if (!owner.IsLoaded)
                return;

            if (update is null)
            {
                if (manual)
                    MessageBox.Show(owner, "사용 가능한 새 버전이 없습니다.", "업데이트 확인");
                return;
            }

            VelopackAsset release = update.TargetFullRelease;
            approved = ConfirmUpdate(owner, manager.CurrentVersion?.ToString() ?? "확인되지 않음", release);
            if (!approved || !confirmRestart())
                return;

            // 작업 저장을 확인한 뒤 업데이트 도중에는 패널을 변경할 수 없게 한다.
            Cursor? originalCursor = owner.Cursor;
            owner.Cursor = Cursors.Wait;
            owner.IsEnabled = false;
            try
            {
                await manager.DownloadUpdatesAsync(update);
                manager.ApplyUpdatesAndRestart(release);
            }
            finally
            {
                owner.IsEnabled = true;
                owner.Cursor = originalCursor;
            }
        }
        catch (Exception error)
        {
            if (owner.IsLoaded && (manual || approved))
                MessageBox.Show(owner, $"업데이트를 완료할 수 없습니다.\n\n{error.Message}", "업데이트 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            checking = false;
        }
    }

    private static bool ConfirmUpdate(Window owner, string currentVersion, VelopackAsset release)
    {
        var dialog = new Window
        {
            Owner = owner,
            Title = "GerberHandler 새 버전",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Width = 540,
            Height = 420,
            MinWidth = 380,
            MinHeight = 260
        };
        var layout = new DockPanel { Margin = new Thickness(20) };
        var header = new TextBlock
        {
            Text = $"현재 버전 {currentVersion} · 새 버전 {release.Version}\n업데이트를 설치하면 앱이 다시 시작됩니다.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var later = new Button { Content = "나중에", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 5, 10, 5) };
        bool hasNotes = !string.IsNullOrWhiteSpace(release.NotesMarkdown);
        var install = new Button { Content = "다운로드 후 설치", IsEnabled = hasNotes, MinWidth = 122, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
        install.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(later);
        buttons.Children.Add(install);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);

        layout.Children.Add(new TextBox
        {
            Text = hasNotes ? release.NotesMarkdown : "이번 버전의 변경 내용이 등록되지 않았습니다.",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8)
        });
        dialog.Content = layout;
        return dialog.ShowDialog() == true;
    }
}
