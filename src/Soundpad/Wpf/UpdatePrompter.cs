using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Soundpad.Update;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace Soundpad.Wpf;

/// <summary>
/// Drives the user-facing update flow on the WPF Dispatcher thread: prompt →
/// download (with a small progress modal) → run installer + exit. Network
/// failures are non-fatal; the user just gets an error and the app keeps
/// running.
///
/// <para>All methods marshal onto <see cref="Application.Current"/>'s
/// Dispatcher, so they're safe to call from the fire-and-forget background
/// check or from a tray-menu click.</para>
/// </summary>
public static class UpdatePrompter
{
    /// <summary>
    /// Runs a check and, if an update is found, prompts the user. Intended for
    /// the silent startup check — pass <paramref name="silentWhenUpToDate"/>
    /// true so it says nothing when there's no update or the network is down.
    /// When false (manual "Verificar atualizações"), it reports "you're up to
    /// date" / "couldn't check" too.
    /// </summary>
    public static async Task CheckAndPromptAsync(Window? owner, bool silentWhenUpToDate)
    {
        UpdateInfo? info;
        try
        {
            info = await new UpdateChecker().CheckAsync().ConfigureAwait(true);
        }
        catch
        {
            info = null; // CheckAsync already swallows, but belt-and-suspenders.
        }

        if (info is null)
        {
            if (!silentWhenUpToDate)
            {
                Post(owner, () => MessageBox.Show(owner!,
                    "Você já está com a versão mais recente do Reson.",
                    "Reson", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            return;
        }

        PromptForUpdate(owner, info);
    }

    private static void PromptForUpdate(Window? owner, UpdateInfo info)
    {
        Post(owner, () =>
        {
            var notes = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? ""
                : "\n\nNovidades:\n" + Truncate(info.ReleaseNotes.Trim(), 600);
            var choice = MessageBox.Show(owner!,
                $"Reson {info.Version} disponível. Atualizar agora?{notes}",
                "Reson — atualização disponível",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
                _ = DownloadAndRunAsync(owner, info);
        });
    }

    private static async Task DownloadAndRunAsync(Window? owner, UpdateInfo info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        Window? progressWin = null;
        ProgressBar? bar = null;

        if (dispatcher is not null)
        {
            dispatcher.Invoke(() =>
            {
                (progressWin, bar) = BuildProgressWindow(owner, info.Version);
                progressWin.Show();
            });
        }

        try
        {
            var checker = new UpdateChecker();
            var progress = new Progress<double>(p =>
            {
                if (bar is null) return;
                dispatcher?.BeginInvoke(new Action(() =>
                {
                    bar.IsIndeterminate = false;
                    bar.Value = Math.Clamp(p * 100, 0, 100);
                }));
            });

            var path = await checker.DownloadAsync(info, progress).ConfigureAwait(true);

            dispatcher?.Invoke(() => { try { progressWin?.Close(); } catch { /* best effort */ } });

            // RunInstallerAndExit calls Environment.Exit, which doesn't return.
            checker.RunInstallerAndExit(path);
        }
        catch (Exception ex)
        {
            dispatcher?.Invoke(() =>
            {
                try { progressWin?.Close(); } catch { /* best effort */ }
                MessageBox.Show(owner!,
                    $"Falha ao baixar a atualização:\n{ex.Message}\n\n" +
                    "Tente novamente mais tarde ou baixe manualmente em github.com/GabrielSoarde/reson/releases.",
                    "Reson", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
    }

    private static (Window, ProgressBar) BuildProgressWindow(Window? owner, string version)
    {
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 22,
            IsIndeterminate = true,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Baixando Reson {version}...",
            Foreground = Brushes.White,
            FontSize = 14,
        });
        panel.Children.Add(bar);

        var win = new Window
        {
            Title = "Reson — atualizando",
            Width = 360,
            Height = 130,
            WindowStartupLocation = owner is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(MainWindow.WindowBg),
            Content = panel,
            ShowInTaskbar = false,
        };
        return (win, bar);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Marshal <paramref name="action"/> onto the WPF Dispatcher. When there's no
    /// running Application (e.g. unexpected call ordering) it runs inline.
    /// </summary>
    private static void Post(Window? owner, Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
