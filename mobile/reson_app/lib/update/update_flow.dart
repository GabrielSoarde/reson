import 'dart:async';

import 'package:flutter/material.dart';
import 'package:ota_update/ota_update.dart';

import '../theme.dart';
import 'update_checker.dart';

/// End-to-end auto-update UX: check → "update available?" dialog → download
/// (with progress) → hand off to the Android package installer.
///
/// All entry points are safe to call fire-and-forget; nothing here throws to
/// the caller. The home screen calls [checkAndPrompt] on load; settings calls
/// [runManualCheck] which surfaces "you're up to date" feedback too.
class UpdateFlow {
  UpdateFlow._();

  /// Startup path: silently check, and only show UI if an update exists.
  static Future<void> checkAndPrompt(BuildContext context) async {
    final info = await UpdateChecker().check();
    if (info == null) return;
    if (!context.mounted) return;
    await _promptAndMaybeInstall(context, info);
  }

  /// Manual "Verificar atualizações" path: show a spinner toast, then either
  /// the update dialog or an "up to date" snackbar.
  static Future<void> runManualCheck(BuildContext context) async {
    final messenger = ScaffoldMessenger.of(context);
    messenger.clearSnackBars();
    messenger.showSnackBar(const SnackBar(
      content: Text('Procurando atualizações...'),
      duration: Duration(seconds: 2),
    ));
    final info = await UpdateChecker().check();
    if (!context.mounted) return;
    if (info == null) {
      messenger.clearSnackBars();
      messenger.showSnackBar(const SnackBar(
        content: Text('Você já está na versão mais recente.'),
      ));
      return;
    }
    await _promptAndMaybeInstall(context, info);
  }

  static Future<void> _promptAndMaybeInstall(
      BuildContext context, UpdateInfo info) async {
    final accepted = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppColors.surface,
        title: Text('Reson ${info.version} disponível'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text('Atualizar agora?',
                  style: TextStyle(color: Colors.white)),
              if (info.notes.isNotEmpty) ...[
                const SizedBox(height: 12),
                Text(
                  _truncateNotes(info.notes),
                  style: const TextStyle(color: AppColors.muted, fontSize: 13),
                ),
              ],
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Depois'),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Sim'),
          ),
        ],
      ),
    );
    if (accepted != true) return;
    if (!context.mounted) return;
    await _downloadAndInstall(context, info);
  }

  /// Shows a non-dismissible progress dialog while ota_update streams the APK,
  /// then closes it once the OS installer intent has been launched. The
  /// installer UI takes over from there ("Install" / "Update" prompt).
  static Future<void> _downloadAndInstall(
      BuildContext context, UpdateInfo info) async {
    final progress = ValueNotifier<double?>(0);
    final messenger = ScaffoldMessenger.of(context);
    StreamSubscription<OtaEvent>? sub;

    void closeDialog() {
      if (Navigator.of(context, rootNavigator: true).canPop()) {
        Navigator.of(context, rootNavigator: true).pop();
      }
    }

    showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppColors.surface,
        title: const Text('Baixando atualização'),
        content: ValueListenableBuilder<double?>(
          valueListenable: progress,
          builder: (ctx, value, _) => Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              LinearProgressIndicator(
                value: value,
                backgroundColor: const Color(0xFF333333),
                color: AppColors.accent,
              ),
              const SizedBox(height: 12),
              Text(
                value == null
                    ? 'Abrindo instalador...'
                    : '${(value * 100).clamp(0, 100).toStringAsFixed(0)}%',
                style: const TextStyle(color: AppColors.muted, fontSize: 13),
              ),
            ],
          ),
        ),
      ),
    );

    try {
      sub = OtaUpdate()
          .execute(info.apkUrl, destinationFilename: 'Reson-Android.apk')
          .listen(
        (event) {
          switch (event.status) {
            case OtaStatus.DOWNLOADING:
              final pct = double.tryParse(event.value ?? '');
              if (pct != null) progress.value = (pct / 100).clamp(0.0, 1.0);
              break;
            case OtaStatus.INSTALLING:
              // Installer intent launched — switch to indeterminate and close
              // shortly; the system UI now owns the flow.
              progress.value = null;
              closeDialog();
              break;
            case OtaStatus.PERMISSION_NOT_GRANTED_ERROR:
              closeDialog();
              messenger.showSnackBar(const SnackBar(
                content: Text(
                    'Permita "instalar apps desconhecidos" para o Reson e tente de novo.'),
              ));
              break;
            case OtaStatus.ALREADY_RUNNING_ERROR:
              closeDialog();
              messenger.showSnackBar(const SnackBar(
                content: Text('Uma atualização já está em andamento.'),
              ));
              break;
            case OtaStatus.DOWNLOAD_ERROR:
            case OtaStatus.CHECKSUM_ERROR:
            case OtaStatus.INSTALLATION_ERROR:
            case OtaStatus.INTERNAL_ERROR:
              closeDialog();
              messenger.showSnackBar(const SnackBar(
                content: Text('Falha ao baixar a atualização.'),
              ));
              break;
            case OtaStatus.CANCELED:
            case OtaStatus.INSTALLATION_DONE:
              closeDialog();
              break;
          }
        },
        onError: (_) {
          closeDialog();
          messenger.showSnackBar(const SnackBar(
            content: Text('Falha ao baixar a atualização.'),
          ));
        },
        onDone: () => sub?.cancel(),
        cancelOnError: true,
      );
    } catch (_) {
      closeDialog();
      messenger.showSnackBar(const SnackBar(
        content: Text('Falha ao iniciar a atualização.'),
      ));
    }
  }

  static String _truncateNotes(String notes, {int max = 280}) {
    final trimmed = notes.trim();
    if (trimmed.length <= max) return trimmed;
    return '${trimmed.substring(0, max).trimRight()}…';
  }
}
