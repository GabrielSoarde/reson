import 'package:flutter/material.dart';

import '../home/home_screen.dart';
import '../storage/config_store.dart';
import '../theme.dart';
import 'manual_entry_screen.dart';
import 'qr_scanner_screen.dart';

/// First-launch screen — primary action is "scan the QR shown on the PC".
///
/// The PC main window shows the QR as a sidebar that encodes
///   http://<lan-ip>:<port>/?t=<token>
/// — we parse that URL on detect (qr_scanner_screen.dart).
class PairingScreen extends StatelessWidget {
  const PairingScreen({super.key});

  /// Common post-pair handoff: load saved config and replace the navigation
  /// stack with HomeScreen so a back-press doesn't return to pairing.
  static Future<void> _gotoHome(BuildContext context) async {
    final cfg = await ConfigStore().load();
    if (!context.mounted || cfg == null) return;
    Navigator.of(context).pushAndRemoveUntil(
      MaterialPageRoute(builder: (_) => HomeScreen(config: cfg)),
      (_) => false,
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppColors.background,
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Spacer(),
              Center(
                child: Image.asset(
                  'assets/reson_logo.png',
                  width: 120,
                  height: 120,
                  filterQuality: FilterQuality.high,
                ),
              ),
              const SizedBox(height: 24),
              const Center(
                child: Text(
                  'RESON',
                  style: TextStyle(
                    fontSize: 28,
                    fontWeight: FontWeight.w800,
                    letterSpacing: 8,
                    color: Colors.white,
                  ),
                ),
              ),
              const SizedBox(height: 12),
              const Center(
                child: Text(
                  'Conecte ao seu PC',
                  style: TextStyle(color: AppColors.muted, fontSize: 15),
                ),
              ),
              const SizedBox(height: 48),
              ElevatedButton.icon(
                onPressed: () async {
                  final ok = await Navigator.of(context).push<bool>(
                    MaterialPageRoute(builder: (_) => const QrScannerScreen()),
                  );
                  if (ok == true && context.mounted) {
                    await _gotoHome(context);
                  }
                },
                icon: const Icon(Icons.qr_code_scanner, size: 26),
                label: const Padding(
                  padding: EdgeInsets.symmetric(vertical: 4),
                  child: Text('Escanear QR Code do PC'),
                ),
              ),
              const SizedBox(height: 16),
              TextButton(
                onPressed: () async {
                  final ok = await Navigator.of(context).push<bool>(
                    MaterialPageRoute(builder: (_) => const ManualEntryScreen()),
                  );
                  if (ok == true && context.mounted) {
                    await _gotoHome(context);
                  }
                },
                child: const Text('Inserir manualmente'),
              ),
              const Spacer(),
              const Center(
                child: Text(
                  'PC e celular devem estar na mesma rede Wi-Fi',
                  style: TextStyle(color: AppColors.muted, fontSize: 12),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
