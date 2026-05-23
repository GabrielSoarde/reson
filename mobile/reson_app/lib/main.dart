import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'home/home_screen.dart';
import 'pairing/pairing_screen.dart';
import 'storage/config_store.dart';
import 'theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  // Force portrait — the grid is designed for portrait orientation. The web UI
  // has a landscape media query, but on Android we get crisper layout by
  // pinning portrait; users still get full landscape via system controls if
  // they really want it (this only sets the preferred default).
  await SystemChrome.setPreferredOrientations([
    DeviceOrientation.portraitUp,
    DeviceOrientation.portraitDown,
  ]);
  // Make the system status bar match our background so the dark wordmark
  // header reads as one continuous strip.
  SystemChrome.setSystemUIOverlayStyle(const SystemUiOverlayStyle(
    statusBarColor: AppColors.background,
    statusBarIconBrightness: Brightness.light,
    systemNavigationBarColor: AppColors.background,
    systemNavigationBarIconBrightness: Brightness.light,
  ));
  runApp(const ResonApp());
}

class ResonApp extends StatelessWidget {
  const ResonApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Reson',
      debugShowCheckedModeBanner: false,
      theme: buildAppTheme(),
      home: const _Boot(),
    );
  }
}

/// Decides the initial route based on persisted pairing config.
///
/// Note: we don't probe the connection here — HomeScreen does its own
/// /api/state fetch on mount and shows a "Não conectado" banner with retry.
/// Probing here would block the splash on a slow network round-trip.
class _Boot extends StatefulWidget {
  const _Boot();

  @override
  State<_Boot> createState() => _BootState();
}

class _BootState extends State<_Boot> {
  final _store = ConfigStore();
  bool _checking = true;
  PairedConfig? _config;

  @override
  void initState() {
    super.initState();
    _check();
  }

  Future<void> _check() async {
    final cfg = await _store.load();
    if (!mounted) return;
    setState(() {
      _config = cfg;
      _checking = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    if (_checking) {
      return const Scaffold(
        body: Center(child: CircularProgressIndicator()),
      );
    }
    if (_config == null) {
      return const PairingScreen();
    }
    return HomeScreen(config: _config!);
  }
}
