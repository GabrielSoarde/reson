import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../api/api_client.dart';
import '../storage/config_store.dart';
import '../theme.dart';

/// Camera-driven QR scanner. The PC's QR encodes a URL like
///   http://192.168.1.42:8080/?t=<token>
///
/// We extract the origin (scheme://host:port) as baseUrl and the `t=` query
/// param as the token. Anything else (file:// URLs, plain text, malformed)
/// is rejected with a snackbar — never silently saved.
class QrScannerScreen extends StatefulWidget {
  const QrScannerScreen({super.key});

  @override
  State<QrScannerScreen> createState() => _QrScannerScreenState();
}

class _QrScannerScreenState extends State<QrScannerScreen> {
  final _controller = MobileScannerController(
    detectionSpeed: DetectionSpeed.normal,
    facing: CameraFacing.back,
  );
  bool _busy = false;
  bool _torch = false;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Future<void> _onDetect(BarcodeCapture capture) async {
    if (_busy) return;
    final raw = capture.barcodes.firstOrNull?.rawValue;
    if (raw == null || raw.isEmpty) return;
    setState(() => _busy = true);

    final parsed = _parseQr(raw);
    if (parsed == null) {
      _toast('QR code não reconhecido');
      await Future<void>.delayed(const Duration(milliseconds: 800));
      if (mounted) setState(() => _busy = false);
      return;
    }

    final (baseUrl, token) = parsed;
    final ok = await probeConnection(baseUrl, token);
    if (!mounted) return;
    if (!ok) {
      _toast('QR code inválido ou Reson offline');
      await Future<void>.delayed(const Duration(milliseconds: 800));
      if (mounted) setState(() => _busy = false);
      return;
    }
    await ConfigStore().save(PairedConfig(baseUrl, token));
    if (!mounted) return;
    Navigator.of(context).pop(true);
  }

  /// Returns (baseUrl, token) or null on any parse failure.
  ///
  /// baseUrl == scheme + host + port (no path, no query). Token comes from the
  /// `t` param in the URL fragment (`#t=`, current) or query (`?t=`, legacy) —
  /// both are accepted for back-compat. We also accept either case for the param
  /// name to be forgiving (the backend emits lowercase, but a stray scanner could
  /// uppercase).
  (String, String)? _parseQr(String raw) {
    try {
      final uri = Uri.parse(raw.trim());
      if (!uri.hasScheme || (uri.scheme != 'http' && uri.scheme != 'https')) return null;
      final frag = Uri.splitQueryString(uri.fragment);
      final token = uri.queryParameters['t'] ?? uri.queryParameters['T'] ?? frag['t'] ?? frag['T'];
      if (token == null || token.isEmpty) return null;
      final host = uri.host;
      if (host.isEmpty) return null;
      final port = uri.hasPort ? uri.port : (uri.scheme == 'https' ? 443 : 80);
      final base = '${uri.scheme}://$host:$port';
      return (base, token);
    } catch (_) {
      return null;
    }
  }

  void _toast(String msg) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).clearSnackBars();
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(msg), duration: const Duration(seconds: 2)),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      appBar: AppBar(
        title: const Text('Escanear QR'),
        backgroundColor: Colors.black,
        actions: [
          IconButton(
            tooltip: 'Lanterna',
            onPressed: () async {
              await _controller.toggleTorch();
              setState(() => _torch = !_torch);
            },
            icon: Icon(_torch ? Icons.flash_on : Icons.flash_off),
          ),
        ],
      ),
      body: Stack(
        children: [
          MobileScanner(
            controller: _controller,
            onDetect: _onDetect,
            errorBuilder: (context, error, child) {
              // Permission denied / no camera / etc. Provide a manual fallback.
              return _PermissionError(
                onManual: () => Navigator.of(context).pop(false),
                message: error.errorDetails?.message ?? error.errorCode.name,
              );
            },
          ),
          IgnorePointer(
            child: Center(
              child: Container(
                width: 260,
                height: 260,
                decoration: BoxDecoration(
                  border: Border.all(color: Colors.white.withValues(alpha: 0.6), width: 2),
                  borderRadius: BorderRadius.circular(16),
                ),
              ),
            ),
          ),
          Positioned(
            left: 0,
            right: 0,
            bottom: 32,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 32),
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
                decoration: BoxDecoration(
                  color: Colors.black.withValues(alpha: 0.6),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: const Text(
                  'Aponte para o QR exibido na tela do PC',
                  textAlign: TextAlign.center,
                  style: TextStyle(color: Colors.white, fontSize: 14),
                ),
              ),
            ),
          ),
          if (_busy)
            Container(
              color: Colors.black54,
              child: const Center(child: CircularProgressIndicator()),
            ),
        ],
      ),
    );
  }
}

class _PermissionError extends StatelessWidget {
  const _PermissionError({required this.onManual, required this.message});
  final VoidCallback onManual;
  final String message;

  @override
  Widget build(BuildContext context) {
    return Container(
      color: AppColors.background,
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(Icons.no_photography, color: AppColors.muted, size: 64),
          const SizedBox(height: 16),
          const Text(
            'Câmera indisponível',
            style: TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.w600),
          ),
          const SizedBox(height: 8),
          Text(message,
              style: const TextStyle(color: AppColors.muted, fontSize: 13),
              textAlign: TextAlign.center),
          const SizedBox(height: 24),
          TextButton(
            onPressed: onManual,
            child: const Text('Voltar e inserir manualmente'),
          ),
        ],
      ),
    );
  }
}
