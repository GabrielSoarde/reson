import 'package:flutter/material.dart';
import 'package:package_info_plus/package_info_plus.dart';

import '../api/api_client.dart';
import '../api/models.dart';
import '../storage/config_store.dart';
import '../theme.dart';
import '../update/update_flow.dart';

/// Settings screen — three device pickers + paired URL + unpair action.
///
/// Pops `true` if the user chose to unpair so HomeScreen can route back to
/// the pairing flow. Otherwise pops with `null` (Settings just edited device
/// assignments; HomeScreen will refetch state on return).
class SettingsScreen extends StatefulWidget {
  const SettingsScreen({super.key, required this.api, required this.state});
  final ApiClient api;
  final StateDto? state;

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  StateDto? _state;
  String? _appVersion;

  @override
  void initState() {
    super.initState();
    _state = widget.state;
    _loadVersion();
    // If we arrived without state (rare), pull it now.
    if (_state == null) _refresh();
  }

  Future<void> _loadVersion() async {
    try {
      final info = await PackageInfo.fromPlatform();
      if (!mounted) return;
      setState(() => _appVersion = '${info.version} (build ${info.buildNumber})');
    } catch (_) {
      if (!mounted) return;
      setState(() => _appVersion = '—');
    }
  }

  Future<void> _refresh() async {
    try {
      final s = await widget.api.fetchState();
      if (!mounted) return;
      setState(() => _state = s);
    } catch (_) {/* leave _state as-is */}
  }

  Future<void> _setGameDevice(String? device) async {
    try {
      await widget.api.setGameDevice(device);
      await _refresh();
    } on ApiException catch (e) {
      _toast(_friendly(e.body, 'Falha ao definir dispositivo de saída'));
    } catch (_) {
      _toast('Erro de rede');
    }
  }

  Future<void> _setMonitorDevice(String? device) async {
    try {
      await widget.api.setMonitorDevice(device);
      // Setting monitor to null also implies monitorEnabled=false in the UX,
      // mirroring the web "Desativado" option.
      if (device == null) {
        try {
          await widget.api.setMonitor(false);
        } catch (_) {/* best-effort */}
      }
      await _refresh();
    } on ApiException catch (e) {
      _toast(_friendly(e.body, 'Falha ao definir dispositivo de monitor'));
    } catch (_) {
      _toast('Erro de rede');
    }
  }

  Future<void> _setMicDevice(String? device) async {
    try {
      await widget.api.setMicDevice(device);
      await _refresh();
    } on ApiException catch (e) {
      _toast(_friendly(e.body, 'Falha ao definir microfone'));
    } catch (_) {
      _toast('Erro de rede');
    }
  }

  String _friendly(String body, String fallback) {
    // The backend wraps errors as { "error": "<code>" }. Translate the known
    // codes to readable PT messages; otherwise show the fallback.
    const map = {
      'monitor_equals_game_device':
          'Monitor não pode ser igual ao dispositivo de saída',
      'game_equals_monitor_device':
          'Saída não pode ser igual ao dispositivo de monitor',
      'unknown_device': 'Dispositivo não encontrado',
    };
    for (final entry in map.entries) {
      if (body.contains(entry.key)) return entry.value;
    }
    return fallback;
  }

  void _toast(String msg) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).clearSnackBars();
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(msg)));
  }

  Future<void> _unpair() async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: AppColors.surface,
        title: const Text('Trocar pareamento?'),
        content: const Text('Você precisará escanear o QR Code do PC de novo.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Cancelar'),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Trocar', style: TextStyle(color: AppColors.danger)),
          ),
        ],
      ),
    );
    if (ok != true) return;
    await ConfigStore().clear();
    if (!mounted) return;
    Navigator.of(context).pop(true); // tell HomeScreen to restart into pairing
  }

  @override
  Widget build(BuildContext context) {
    final s = _state;
    return Scaffold(
      backgroundColor: AppColors.background,
      appBar: AppBar(title: const Text('Configurações')),
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            _section('Conexão'),
            _kv('URL pareada', widget.api.baseUrl),
            const SizedBox(height: 12),
            OutlinedButton.icon(
              onPressed: _unpair,
              icon: const Icon(Icons.link_off, color: AppColors.danger),
              label: const Text('Trocar pareamento',
                  style: TextStyle(color: AppColors.danger)),
              style: OutlinedButton.styleFrom(
                side: const BorderSide(color: AppColors.danger),
                padding: const EdgeInsets.symmetric(vertical: 12),
              ),
            ),
            const SizedBox(height: 24),
            _section('Dispositivos de áudio'),
            if (s == null)
              const Padding(
                padding: EdgeInsets.symmetric(vertical: 24),
                child: Center(child: CircularProgressIndicator()),
              )
            else ...[
              _DevicePicker(
                label: 'Dispositivo de saída (game)',
                value: s.audioDevice,
                valueName: s.audioDeviceName,
                options: s.availableOutputDevices,
                onChanged: _setGameDevice,
                nullLabel: null, // game device cannot be null
              ),
              const SizedBox(height: 12),
              _DevicePicker(
                label: 'Dispositivo de monitor (fone)',
                value: s.monitorDevice,
                valueName: s.monitorDeviceName,
                options: s.availableOutputDevices,
                onChanged: _setMonitorDevice,
                nullLabel: 'Desativado',
              ),
              const SizedBox(height: 12),
              _DevicePicker(
                label: 'Microfone',
                value: s.micDevice,
                valueName: s.micDeviceName,
                options: s.availableInputDevices,
                onChanged: _setMicDevice,
                nullLabel: 'Padrão do Windows',
              ),
            ],
            const SizedBox(height: 32),
            _section('Sobre'),
            _kv('Versão', _appVersion ?? '...'),
            const SizedBox(height: 12),
            OutlinedButton.icon(
              onPressed: () => UpdateFlow.runManualCheck(context),
              icon: const Icon(Icons.system_update, color: AppColors.accent),
              label: const Text('Verificar atualizações',
                  style: TextStyle(color: AppColors.accent)),
              style: OutlinedButton.styleFrom(
                side: const BorderSide(color: AppColors.border),
                padding: const EdgeInsets.symmetric(vertical: 12),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _section(String title) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Text(
        title.toUpperCase(),
        style: const TextStyle(
          color: AppColors.muted,
          fontSize: 11,
          fontWeight: FontWeight.w700,
          letterSpacing: 2,
        ),
      ),
    );
  }

  Widget _kv(String k, String v) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(width: 120, child: Text(k, style: const TextStyle(color: AppColors.muted))),
          Expanded(
            child: Text(v, style: const TextStyle(color: Colors.white)),
          ),
        ],
      ),
    );
  }
}

/// Generic device dropdown. `value` is the persisted endpoint id (stable),
/// `valueName` is the resolved FriendlyName (for display only). If the device
/// is unplugged, valueName is null — we still keep the id selected and show
/// "(desconectado)".
class _DevicePicker extends StatelessWidget {
  const _DevicePicker({
    required this.label,
    required this.value,
    required this.valueName,
    required this.options,
    required this.onChanged,
    required this.nullLabel,
  });

  final String label;
  final String? value;
  final String? valueName;
  final List<String> options;
  final void Function(String? device) onChanged;

  /// Label for the "no device" option, or null to hide that option entirely
  /// (game device cannot be null).
  final String? nullLabel;

  @override
  Widget build(BuildContext context) {
    // Build the dropdown options: list of FriendlyNames + (optional) null entry.
    // We send the FriendlyName back to the server — it accepts both ids and
    // names per ResolveDeviceInput (see PlaybackEndpoints.cs).
    final items = <DropdownMenuItem<String?>>[];
    if (nullLabel != null) {
      items.add(DropdownMenuItem<String?>(
        value: '__null__',
        child: Text(nullLabel!,
            style: const TextStyle(fontStyle: FontStyle.italic, color: AppColors.muted)),
      ));
    }
    for (final name in options) {
      items.add(DropdownMenuItem<String?>(value: name, child: Text(name, maxLines: 1, overflow: TextOverflow.ellipsis)));
    }
    // Selected entry: if a value is set, surface its FriendlyName (or
    // "(desconectado)" placeholder when unplugged). The dropdown value is
    // matched against `name`, not the id, because that's what's in `items`.
    final selectedName = value == null ? '__null__' : valueName;
    // If the current device isn't in the available list (unplugged), inject
    // a disabled-style entry so the picker can display it.
    if (value != null && (selectedName == null || !options.contains(selectedName))) {
      items.insert(
        nullLabel == null ? 0 : 1,
        DropdownMenuItem<String?>(
          value: selectedName ?? '__missing__',
          child: Text(
            selectedName == null ? '(desconectado)' : '$selectedName (desconectado)',
            style: const TextStyle(color: AppColors.muted),
          ),
        ),
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: const TextStyle(color: AppColors.muted, fontSize: 12)),
        const SizedBox(height: 6),
        Container(
          decoration: BoxDecoration(
            color: AppColors.surface,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: AppColors.border),
          ),
          padding: const EdgeInsets.symmetric(horizontal: 12),
          child: DropdownButtonHideUnderline(
            child: DropdownButton<String?>(
              value: selectedName ?? (value != null ? '__missing__' : '__null__'),
              isExpanded: true,
              dropdownColor: AppColors.surface,
              iconEnabledColor: AppColors.muted,
              style: const TextStyle(color: Colors.white, fontSize: 14),
              items: items,
              onChanged: (v) {
                if (v == '__null__') {
                  onChanged(null);
                } else if (v == '__missing__') {
                  // No-op — user picked the disabled "(desconectado)" entry.
                } else {
                  onChanged(v);
                }
              },
            ),
          ),
        ),
      ],
    );
  }
}
