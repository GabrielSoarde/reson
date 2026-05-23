using System.Windows;
using Soundpad.Update;
using Velopack; // UpdateInfo / VelopackAsset returned by VelopackUpdater
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Soundpad.Wpf;

/// <summary>
/// Drives the user-facing Velopack update flow on the WPF Dispatcher thread.
///
/// <para><b>Silent startup path</b> (<paramref name="silentWhenUpToDate"/> = true):
/// check → if an update exists, download the delta in the background (no UI) →
/// then ask once "Reiniciar para atualizar?". Sim → apply + restart (no UAC, no
/// wizard). If there's no update / offline / this isn't a Velopack install, it
/// says nothing.</para>
///
/// <para><b>Manual path</b> ("Verificar atualizações" tray entry,
/// <paramref name="silentWhenUpToDate"/> = false): additionally reports "you're
/// up to date" and explains when the app isn't Velopack-managed (old Inno
/// install) so the user knows why nothing happens.</para>
///
/// <para>All UI is marshaled onto <see cref="Application.Current"/>'s Dispatcher,
/// so it's safe to call from the fire-and-forget startup check or a tray click.
/// Network errors are swallowed.</para>
/// </summary>
public static class VelopackUpdatePrompter
{
    public static async Task CheckAndPromptAsync(Window? owner, bool silentWhenUpToDate)
    {
        var updater = new VelopackUpdater();

        // Old Inno-installed users (and `dotnet run`) aren't Velopack-managed.
        // Auto-updates can't apply to them; tell them only on a manual check.
        if (!updater.IsInstalled)
        {
            if (!silentWhenUpToDate)
            {
                Post(owner, () => MessageBox.Show(owner!,
                    "Atualizações automáticas ficam disponíveis a partir da próxima " +
                    "instalação do Reson (novo instalador). Baixe a versão mais recente " +
                    "em github.com/GabrielSoarde/reson/releases — depois dela, o Reson " +
                    "se atualiza sozinho, sem pedir permissão.",
                    "Reson", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            return;
        }

        UpdateInfo? info;
        try
        {
            info = await updater.CheckAsync().ConfigureAwait(true);
        }
        catch
        {
            info = null; // CheckAsync already swallows; belt-and-suspenders.
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

        // Download the delta quietly in the background, then prompt to restart.
        try
        {
            await updater.DownloadAsync(info).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!silentWhenUpToDate)
            {
                Post(owner, () => MessageBox.Show(owner!,
                    $"Falha ao baixar a atualização:\n{ex.Message}\n\n" +
                    "Tente novamente mais tarde.",
                    "Reson", MessageBoxButton.OK, MessageBoxImage.Error));
            }
            return;
        }

        var version = info.TargetFullRelease?.Version?.ToString() ?? "";
        Post(owner, () =>
        {
            var choice = MessageBox.Show(owner!,
                $"Uma nova versão do Reson ({version}) foi baixada.\n\n" +
                "Reiniciar para atualizar agora?",
                "Reson — atualização pronta",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
            {
                try
                {
                    // Does not return on success — Velopack swaps the binaries
                    // and relaunches Reson.exe. No UAC, no wizard.
                    updater.ApplyAndRestart(info);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner!,
                        $"Não consegui aplicar a atualização:\n{ex.Message}\n\n" +
                        "Ela será aplicada automaticamente na próxima vez que você " +
                        "abrir o Reson.",
                        "Reson", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            // If "Não": the staged update stays on disk and Velopack applies it
            // on the next normal launch — nothing more to do.
        });
    }

    /// <summary>
    /// Marshal <paramref name="action"/> onto the WPF Dispatcher. When there's no
    /// running Application (unexpected call ordering) it runs inline.
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
