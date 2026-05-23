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
import '../update/update_flow.dart';
import '../upload/upload_screen.dart';
import 'board_drawer.dart';
import 'bottom_controls.dart';
import 'sound_editor_sheet.dart';
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
  bool _editing = false;

  /// Guards the startup auto-update check so it fires at most once per app
  /// session (the first time the main UI renders), not on every reconnect.
  bool _updateChecked = false;

  final _scaffoldKey = GlobalKey<ScaffoldState>();

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
      _maybeCheckForUpdate();
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

  /// Fire-and-forget GitHub Releases check, once per session, after the main
  /// UI is up. A short delay lets the first frame settle before any dialog.
  void _maybeCheckForUpdate() {
    if (_updateChecked) return;
    _updateChecked = true;
    Future.delayed(const Duration(milliseconds: 600), () {
      if (!mounted) return;
      // checkAndPrompt swallows all errors and only shows UI if there's an
      // update — safe to ignore the returned future.
      unawaited(UpdateFlow.checkAndPrompt(context));
    });
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
      case 'activeBoardChanged':
      case 'boardsChanged':
        // Full re-fetch — the broadcast carries no (usable) diff payload and
        // these changes can ripple into device-name fields, the board list, or
        // the visible sound set (boards scope which sounds /api/state returns).
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
        boards: s.boards,
        activeBoardId: s.activeBoardId,
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
        boards: s.boards,
        activeBoardId: s.activeBoardId,
      );

  StateDto _patchSounds(StateDto s, List<SoundEntryDto> sounds) => StateDto(
        audioDevice: s.audioDevice,
        audioDeviceName: s.audioDeviceName,
        monitorDevice: s.monitorDevice,
        monitorDeviceName: s.monitorDeviceName,
        monitorEnabled: s.monitorEnabled,
        micDevice: s.micDevice,
        micDeviceName: s.micDeviceName,
        availableOutputDevices: s.availableOutputDevices,
        availableInputDevices: s.availableInputDevices,
        volume: s.volume,
        grid: s.grid,
        sounds: sounds,
        nowPlaying: s.nowPlaying,
        authRequired: s.authRequired,
        boards: s.boards,
        activeBoardId: s.activeBoardId,
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

  // --- boards ---------------------------------------------------------------

  Future<void> _activateBoard(BoardDto b) async {
    if (b.id == _state?.activeBoardId) return;
    try {
      await _api.activateBoard(b.id);
      // WS activeBoardChanged will trigger a refetch, but refetch eagerly too
      // so the switch feels instant even if the socket is momentarily down.
      await _refreshState();
    } catch (_) {
      _toast('Falha ao trocar de board');
    }
  }

  Future<void> _createBoard() async {
    final result = await _showBoardDialog(title: 'Novo board');
    if (result == null) return;
    try {
      await _api.createBoard(result.name, result.color);
      await _refreshState();
    } catch (_) {
      _toast('Falha ao criar board');
    }
  }

  Future<void> _renameBoard(BoardDto b) async {
    final controller = TextEditingController(text: b.name);
    final name = await showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppColors.surface,
        title: const Text('Renomear board'),
        content: TextField(
          controller: controller,
          autofocus: true,
          textCapitalization: TextCapitalization.sentences,
          decoration: const InputDecoration(hintText: 'Nome'),
          onSubmitted: (v) => Navigator.of(ctx).pop(v.trim()),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('Cancelar'),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(controller.text.trim()),
            child: const Text('Salvar'),
          ),
        ],
      ),
    );
    if (name == null || name.isEmpty || name == b.name) return;
    try {
      await _api.renameBoard(b.id, name: name);
      await _refreshState();
    } catch (_) {
      _toast('Falha ao renomear');
    }
  }

  Future<void> _recolorBoard(BoardDto b) async {
    final color = await showDialog<String>(
      context: context,
      builder: (ctx) {
        var selected = b.color;
        return StatefulBuilder(
          builder: (ctx, setLocal) => AlertDialog(
            backgroundColor: AppColors.surface,
            title: const Text('Mudar cor'),
            content: PaletteGrid(
              selected: selected,
              onPick: (c) => setLocal(() => selected = c),
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.of(ctx).pop(),
                child: const Text('Cancelar'),
              ),
              TextButton(
                onPressed: () => Navigator.of(ctx).pop(selected),
                child: const Text('Salvar'),
              ),
            ],
          ),
        );
      },
    );
    if (color == null || color == b.color) return;
    try {
      await _api.renameBoard(b.id, color: color);
      await _refreshState();
    } catch (_) {
      _toast('Falha ao mudar cor');
    }
  }

  Future<void> _deleteBoard(BoardDto b) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppColors.surface,
        title: const Text('Apagar board?'),
        content: Text('"${b.name}" e todos os sons nele serão removidos.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Cancelar'),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Apagar', style: TextStyle(color: AppColors.danger)),
          ),
        ],
      ),
    );
    if (ok != true) return;
    try {
      await _api.deleteBoard(b.id);
      await _refreshState();
    } on ApiException catch (e) {
      _toast(e.statusCode == 400
          ? 'Não é possível apagar o último board'
          : 'Falha ao apagar board');
    } catch (_) {
      _toast('Falha ao apagar board');
    }
  }

  /// Shared name+color dialog for board creation. Returns null on cancel.
  Future<({String name, String color})?> _showBoardDialog({
    required String title,
  }) async {
    final controller = TextEditingController();
    return showDialog<({String name, String color})>(
      context: context,
      builder: (ctx) {
        var selected = kBoardPalette[5]; // default #3b82f6 (accent)
        return StatefulBuilder(
          builder: (ctx, setLocal) => AlertDialog(
            backgroundColor: AppColors.surface,
            title: Text(title),
            content: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                TextField(
                  controller: controller,
                  autofocus: true,
                  textCapitalization: TextCapitalization.sentences,
                  decoration: const InputDecoration(hintText: 'Nome do board'),
                ),
                const SizedBox(height: 20),
                PaletteGrid(
                  selected: selected,
                  onPick: (c) => setLocal(() => selected = c),
                ),
              ],
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.of(ctx).pop(),
                child: const Text('Cancelar'),
              ),
              TextButton(
                onPressed: () {
                  final name = controller.text.trim();
                  if (name.isEmpty) return;
                  Navigator.of(ctx).pop((name: name, color: selected));
                },
                child: const Text('Criar'),
              ),
            ],
          ),
        );
      },
    );
  }

  // --- per-sound ------------------------------------------------------------

  void _openSoundEditor(SoundEntryDto sound) {
    showModalBottomSheet<void>(
      context: context,
      backgroundColor: AppColors.surface,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (ctx) => SoundEditorSheet(
        sound: sound,
        onVolumeChanged: (v) => _setSoundVolume(sound.id, v),
        onDelete: () {
          Navigator.of(ctx).pop();
          _confirmDeleteSound(sound);
        },
      ),
    );
  }

  Future<void> _setSoundVolume(String soundId, int value) async {
    // Optimistic local patch so the badge/slider don't snap back. The backend
    // broadcasts libraryChanged (not echo-filtered), so a refetch will follow
    // and reconcile — but the optimistic update keeps the UI smooth meanwhile.
    if (_state != null) {
      final next = _state!.sounds
          .map((s) => s.id == soundId ? s.copyWith(volume: value) : s)
          .toList();
      setState(() => _state = _patchSounds(_state!, next));
    }
    try {
      await _api.setSoundVolume(soundId, value);
    } catch (_) {
      _toast('Falha ao mudar volume do som');
    }
  }

  Future<void> _confirmDeleteSound(SoundEntryDto sound) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppColors.surface,
        title: const Text('Apagar som?'),
        content: Text('"${sound.label}" será removido permanentemente.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Cancelar'),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Apagar', style: TextStyle(color: AppColors.danger)),
          ),
        ],
      ),
    );
    if (ok != true) return;
    try {
      await _api.deleteSound(sound.id);
      await _refreshState();
    } catch (_) {
      _toast('Falha ao apagar som');
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
        _state = _patchSounds(_state!, next);
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
    final boards = _state?.boards ?? const <BoardDto>[];
    final activeBoard = _state?.activeBoard;
    // App bar title is the active board's name; fall back to the wordmark when
    // we have no boards yet (loading / not connected).
    final titleText = activeBoard?.name ?? 'RESON';
    return Scaffold(
      key: _scaffoldKey,
      backgroundColor: AppColors.background,
      drawer: BoardDrawer(
        boards: boards,
        activeBoardId: _state?.activeBoardId,
        onActivate: (b) {
          Navigator.of(context).pop(); // close drawer
          _activateBoard(b);
        },
        onRename: _renameBoard,
        onRecolor: _recolorBoard,
        onDelete: _deleteBoard,
        onCreate: () {
          Navigator.of(context).pop(); // close drawer before dialog
          _createBoard();
        },
      ),
      appBar: AppBar(
        leading: IconButton(
          tooltip: 'Boards',
          icon: const Icon(Icons.menu),
          onPressed: () => _scaffoldKey.currentState?.openDrawer(),
        ),
        title: Row(
          children: [
            Flexible(
              child: Text(
                titleText,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: activeBoard == null
                    ? kWordmarkStyle
                    : const TextStyle(
                        fontSize: 18,
                        fontWeight: FontWeight.w700,
                        color: Colors.white,
                      ),
              ),
            ),
            const SizedBox(width: 10),
            _ConnectionDot(connected: _wsConnected),
          ],
        ),
        actions: [
          IconButton(
            tooltip: _editing ? 'Sair da edição' : 'Editar sons',
            onPressed: _state == null
                ? null
                : () => setState(() => _editing = !_editing),
            icon: Icon(_editing ? Icons.check : Icons.edit,
                color: _editing ? AppColors.accent : null),
          ),
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
      editing: _editing,
      onEdit: _openSoundEditor,
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
