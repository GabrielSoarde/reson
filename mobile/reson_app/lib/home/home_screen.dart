import 'dart:async';

import 'package:flutter/material.dart';
import 'package:vibration/vibration.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../api/ws_client.dart';
import '../pairing/pairing_screen.dart';
import '../settings/settings_screen.dart';
import '../storage/config_store.dart';
import '../theme.dart';
import '../upload/upload_screen.dart';
import 'bottom_controls.dart';
import 'sound_grid.dart';

/// Main app screen: header (wordmark + connection dot + settings) +
/// sound grid + bottom controls. Owns the ApiClient and WsClient lifecycle.
///
/// State model:
/// - [_state] is the authoritative snapshot, refreshed by /api/state and
///   patched in place by WS events (playing/stopped/volumeChanged/etc.).
/// - libraryChanged → re-fetch /api/state (the broadcast carries no diff).
/// - On 401 from any HTTP call we treat it as "session invalidated" — show a
///   banner and let the user re-pair. Not auto-clearing storage because the
///   server might just be down for a minute and the token still valid.
class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key, required this.config});
  final PairedConfig config;

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  late ApiClient _api;
  WsClient? _ws;

  StateDto? _state;
  String? _nowPlaying;
  bool _wsConnected = false;
  String? _bannerError;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _api = ApiClient(baseUrl: widget.config.url, token: widget.config.token);
    _bootstrap();
  }

  @override
  void dispose() {
    _ws?.dispose();
    _api.close();
    super.dispose();
  }

  Future<void> _bootstrap() async {
    setState(() {
      _loading = true;
      _bannerError = null;
    });
    try {
      final s = await _api.fetchState();
      if (!mounted) return;
      setState(() {
        _state = s;
        _nowPlaying = s.nowPlaying;
        _loading = false;
      });
      _connectWs();
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _bannerError = e.statusCode == 401
            ? 'Sessão expirada. Refaça o pareamento.'
            : 'Não conectado (HTTP ${e.statusCode})';
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _bannerError = 'Não conectado. Verifique se o PC está ligado e na mesma rede.';
      });
    }
  }

  void _connectWs() {
    _ws?.dispose();
    _ws = WsClient(
      baseUrl: widget.config.url,
      token: widget.config.token,
      originId: _api.originId,
    );
    _ws!.connectionState.listen((up) {
      if (!mounted) return;
      setState(() => _wsConnected = up);
    });
    _ws!.events.listen(_onWsEvent);
    _ws!.start();
  }

  void _onWsEvent(WsEvent e) {
    if (!mounted || _state == null) return;
    switch (e.type) {
      case 'playing':
        setState(() => _nowPlaying = e.payload['soundId'] as String?);
        break;
      case 'stopped':
        setState(() => _nowPlaying = null);
        break;
      case 'volumeChanged':
        final v = (e.payload['value'] as num?)?.toInt();
        if (v != null) setState(() => _state = _patchVolume(_state!, v));
        break;
      case 'monitorChanged':
        final enabled = e.payload['enabled'] as bool?;
        if (enabled != null) {
          setState(() => _state = _patchMonitor(_state!, enabled));
        }
        break;
      case 'libraryChanged':
      case 'gameDeviceChanged':
      case 'monitorDeviceChanged':
      case 'micDeviceChanged':
        // Full re-fetch — the broadcast carries no diff payload and these
        // changes can ripple into the device-name fields in StateDto.
        _refreshState();
        break;
    }
  }

  Future<void> _refreshState() async {
    try {
      final s = await _api.fetchState();
      if (!mounted) return;
      setState(() {
        _state = s;
        _nowPlaying = s.nowPlaying;
      });
    } catch (_) {
      // Silent — WS reconnect loop will surface persistent breakage.
    }
  }

  StateDto _patchVolume(StateDto s, int v) => StateDto(
        audioDevice: s.audioDevice,
        audioDeviceName: s.audioDeviceName,
        monitorDevice: s.monitorDevice,
        monitorDeviceName: s.monitorDeviceName,
        monitorEnabled: s.monitorEnabled,
        micDevice: s.micDevice,
        micDeviceName: s.micDeviceName,
        availableOutputDevices: s.availableOutputDevices,
        availableInputDevices: s.availableInputDevices,
        volume: v,
        grid: s.grid,
        sounds: s.sounds,
        nowPlaying: s.nowPlaying,
        authRequired: s.authRequired,
      );

  StateDto _patchMonitor(StateDto s, bool enabled) => StateDto(
        audioDevice: s.audioDevice,
        audioDeviceName: s.audioDeviceName,
        monitorDevice: s.monitorDevice,
        monitorDeviceName: s.monitorDeviceName,
        monitorEnabled: enabled,
        micDevice: s.micDevice,
        micDeviceName: s.micDeviceName,
        availableOutputDevices: s.availableOutputDevices,
        availableInputDevices: s.availableInputDevices,
        volume: s.volume,
        grid: s.grid,
        sounds: s.sounds,
        nowPlaying: s.nowPlaying,
        authRequired: s.authRequired,
      );

  // --- user actions ---------------------------------------------------------

  Future<void> _playSound(SoundEntryDto s) async {
    // Tiny haptic — matches web vibrate(15).
    final hasVib = await Vibration.hasVibrator() ?? false;
    if (hasVib) Vibration.vibrate(duration: 15);
    try {
      await _api.play(s.id);
    } catch (_) {
      _toast('Falha ao tocar');
    }
  }

  Future<void> _stop() async {
    try {
      await _api.stop();
    } catch (_) {
      _toast('Falha ao parar');
    }
  }

  Future<void> _setVolume(int v) async {
    // Optimistic local update so the slider doesn't snap back.
    if (_state != null) {
      setState(() => _state = _patchVolume(_state!, v));
    }
    try {
      await _api.setVolume(v);
    } catch (_) {
      _toast('Falha ao mudar volume');
    }
  }

  Future<void> _setMonitor(bool enabled) async {
    if (_state != null) {
      setState(() => _state = _patchMonitor(_state!, enabled));
    }
    try {
      await _api.setMonitor(enabled);
    } catch (_) {
      _toast('Falha no monitor');
    }
  }

  Future<void> _onLayoutChanged(List<LayoutPlacement> placements) async {
    // Optimistic local update: apply placements to our state, render, then
    // POST. On failure, refetch to recover the canonical layout.
    if (_state != null) {
      final byId = {for (final p in placements) p.id: p.position};
      final next = _state!.sounds.map((s) {
        if (byId.containsKey(s.id)) {
          return s.copyWith(position: byId[s.id], clearPosition: byId[s.id] == null);
        }
        return s;
      }).toList();
      setState(() {
        _state = StateDto(
          audioDevice: _state!.audioDevice,
          audioDeviceName: _state!.audioDeviceName,
          monitorDevice: _state!.monitorDevice,
          monitorDeviceName: _state!.monitorDeviceName,
          monitorEnabled: _state!.monitorEnabled,
          micDevice: _state!.micDevice,
          micDeviceName: _state!.micDeviceName,
          availableOutputDevices: _state!.availableOutputDevices,
          availableInputDevices: _state!.availableInputDevices,
          volume: _state!.volume,
          grid: _state!.grid,
          sounds: next,
          nowPlaying: _state!.nowPlaying,
          authRequired: _state!.authRequired,
        );
      });
    }
    try {
      await _api.applyLayout(placements);
    } catch (_) {
      _toast('Falha ao reordenar');
      await _refreshState();
    }
  }

  void _toast(String msg) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).clearSnackBars();
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(msg), duration: const Duration(seconds: 2)),
    );
  }

  Future<void> _openUpload() async {
    await Navigator.of(context).push(MaterialPageRoute(
      builder: (_) => UploadScreen(api: _api),
    ));
    // No explicit refresh needed — successful uploads broadcast libraryChanged
    // and our WS listener refetches state. Refetch anyway as a safety net in
    // case the WS dropped during the upload.
    await _refreshState();
  }

  Future<void> _openSettings() async {
    final shouldUnpair = await Navigator.of(context).push<bool>(MaterialPageRoute(
      builder: (_) => SettingsScreen(
        api: _api,
        state: _state,
      ),
    ));
    if (shouldUnpair == true && mounted) {
      // ConfigStore was cleared in SettingsScreen — go back to pairing.
      Navigator.of(context).pushAndRemoveUntil(
        MaterialPageRoute(builder: (_) => const _Restart()),
        (_) => false,
      );
    } else {
      // Settings might have changed device assignments — refresh to pick up
      // new *DeviceName fields.
      await _refreshState();
    }
  }

  // --- build ---------------------------------------------------------------

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppColors.background,
      appBar: AppBar(
        title: Row(
          children: [
            const Text('RESON', style: kWordmarkStyle),
            const SizedBox(width: 10),
            _ConnectionDot(connected: _wsConnected),
          ],
        ),
        actions: [
          IconButton(
            tooltip: 'Configurações',
            onPressed: _openSettings,
            icon: const Icon(Icons.settings),
          ),
        ],
      ),
      body: SafeArea(
        top: false,
        child: Column(
          children: [
            if (_bannerError != null)
              Container(
                width: double.infinity,
                color: AppColors.danger.withValues(alpha: 0.18),
                padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                child: Row(
                  children: [
                    const Icon(Icons.wifi_off, color: AppColors.danger, size: 18),
                    const SizedBox(width: 8),
                    Expanded(
                        child: Text(_bannerError!,
                            style: const TextStyle(color: Colors.white, fontSize: 13))),
                    TextButton(onPressed: _bootstrap, child: const Text('Tentar de novo')),
                  ],
                ),
              ),
            Expanded(child: _buildBody()),
            if (_state != null)
              BottomControls(
                volume: _state!.volume,
                monitorEnabled: _state!.monitorEnabled,
                onStop: _stop,
                onVolumeChanged: _setVolume,
                onMonitorChanged: _setMonitor,
                onUpload: _openUpload,
                onSettings: _openSettings,
              ),
          ],
        ),
      ),
    );
  }

  Widget _buildBody() {
    if (_loading) return const Center(child: CircularProgressIndicator());
    if (_state == null) {
      return const Center(
        child: Padding(
          padding: EdgeInsets.all(24),
          child: Text(
            'Não conectado.',
            style: TextStyle(color: AppColors.muted, fontSize: 14),
          ),
        ),
      );
    }
    return SoundGrid(
      cols: _state!.grid.cols,
      rows: _state!.grid.rows,
      sounds: _state!.sounds,
      nowPlaying: _nowPlaying,
      onPlay: _playSound,
      onLayoutChanged: _onLayoutChanged,
    );
  }
}

class _ConnectionDot extends StatelessWidget {
  const _ConnectionDot({required this.connected});
  final bool connected;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 10,
      height: 10,
      decoration: BoxDecoration(
        color: connected ? AppColors.online : AppColors.offline,
        shape: BoxShape.circle,
        boxShadow: [
          BoxShadow(
            color: (connected ? AppColors.online : AppColors.offline).withValues(alpha: 0.5),
            blurRadius: 4,
          ),
        ],
      ),
    );
  }
}

/// Trampoline shown after "Trocar pareamento" — pops back to the pairing
/// flow. We use a trampoline instead of popping the stack so the disposal of
/// _HomeScreenState (closing ApiClient + WS) happens cleanly during the
/// transition rather than after PairingScreen is already on screen.
class _Restart extends StatelessWidget {
  const _Restart();
  @override
  Widget build(BuildContext context) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      Navigator.of(context).pushReplacement(
        MaterialPageRoute(builder: (_) => const PairingScreen()),
      );
    });
    return const Scaffold(body: Center(child: CircularProgressIndicator()));
  }
}
