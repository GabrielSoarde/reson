import 'dart:async';

import 'package:flutter/material.dart';
import 'package:vibration/vibration.dart';

import '../theme.dart';

/// Bottom bar: STOP / volume slider / monitor / upload / settings.
///
/// Volume slider follows the web app's debounce model: the local value updates
/// instantly on drag but the network POST is deferred 100ms so we don't flood
/// the server during a slow drag. We also push immediately on drag end so the
/// final value is always committed.
class BottomControls extends StatefulWidget {
  const BottomControls({
    super.key,
    required this.volume,
    required this.monitorEnabled,
    required this.onStop,
    required this.onVolumeChanged,
    required this.onMonitorChanged,
    required this.onUpload,
    required this.onSettings,
  });

  final int volume;
  final bool monitorEnabled;
  final VoidCallback onStop;
  final void Function(int value) onVolumeChanged;
  final void Function(bool enabled) onMonitorChanged;
  final VoidCallback onUpload;
  final VoidCallback onSettings;

  @override
  State<BottomControls> createState() => _BottomControlsState();
}

class _BottomControlsState extends State<BottomControls> {
  late double _localVolume;
  Timer? _debounce;

  @override
  void initState() {
    super.initState();
    _localVolume = widget.volume.toDouble();
  }

  @override
  void didUpdateWidget(covariant BottomControls old) {
    super.didUpdateWidget(old);
    // Adopt server-pushed volume changes only when we're not in the middle of
    // a drag — otherwise the thumb would jump back during a fast slide.
    if (_debounce == null && widget.volume.toDouble() != _localVolume) {
      _localVolume = widget.volume.toDouble();
    }
  }

  @override
  void dispose() {
    _debounce?.cancel();
    super.dispose();
  }

  void _onChanged(double v) {
    setState(() => _localVolume = v);
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 100), () {
      _debounce = null;
      widget.onVolumeChanged(v.round());
    });
  }

  void _onChangeEnd(double v) {
    _debounce?.cancel();
    _debounce = null;
    widget.onVolumeChanged(v.round());
  }

  Future<void> _onStopPressed() async {
    final hasVib = await Vibration.hasVibrator() ?? false;
    if (hasVib) {
      // Double-pulse, same intent as web: vibrate([30, 40, 30]).
      Vibration.vibrate(pattern: [0, 30, 40, 30]);
    }
    widget.onStop();
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: AppColors.background,
        border: Border(top: BorderSide(color: AppColors.border)),
      ),
      padding: EdgeInsets.fromLTRB(
        16,
        12,
        16,
        12 + MediaQuery.of(context).padding.bottom,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          SizedBox(
            width: double.infinity,
            child: Material(
              color: AppColors.danger,
              borderRadius: BorderRadius.circular(10),
              child: InkWell(
                onTap: _onStopPressed,
                borderRadius: BorderRadius.circular(10),
                splashColor: Colors.white.withValues(alpha: 0.2),
                child: const Padding(
                  padding: EdgeInsets.symmetric(vertical: 16),
                  child: Center(
                    child: Text(
                      '■ STOP',
                      style: TextStyle(
                        color: Colors.white,
                        fontWeight: FontWeight.w800,
                        fontSize: 16,
                        letterSpacing: 1,
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              const Icon(Icons.volume_down, color: AppColors.muted, size: 20),
              Expanded(
                child: Slider(
                  value: _localVolume.clamp(0, 100),
                  min: 0,
                  max: 100,
                  onChanged: _onChanged,
                  onChangeEnd: _onChangeEnd,
                ),
              ),
              SizedBox(
                width: 44,
                child: Text(
                  '${_localVolume.round()}%',
                  textAlign: TextAlign.end,
                  style: const TextStyle(color: AppColors.muted, fontWeight: FontWeight.w600),
                ),
              ),
            ],
          ),
          Row(
            children: [
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Text('🎧 Monitor',
                      style: TextStyle(color: Colors.white, fontWeight: FontWeight.w500)),
                  Switch(
                    value: widget.monitorEnabled,
                    onChanged: widget.onMonitorChanged,
                  ),
                ],
              ),
              const Spacer(),
              IconButton(
                tooltip: 'Adicionar som',
                onPressed: widget.onUpload,
                icon: const Icon(Icons.add_circle, color: AppColors.success, size: 32),
              ),
              IconButton(
                tooltip: 'Configurações',
                onPressed: widget.onSettings,
                icon: const Icon(Icons.settings, color: Colors.white70, size: 26),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
