import 'dart:async';

import 'package:flutter/material.dart';

import '../api/models.dart';
import '../theme.dart';

/// Bottom sheet shown when a tile is tapped in edit mode. Hosts a per-sound
/// volume slider (0-100, debounced 150ms → onVolumeChanged) and an "Apagar"
/// button.
///
/// The slider updates the local value instantly and pushes the network call
/// after a 150ms idle; we also push on drag end so the final value always
/// commits. Mirrors the master-volume debounce model in BottomControls.
class SoundEditorSheet extends StatefulWidget {
  const SoundEditorSheet({
    super.key,
    required this.sound,
    required this.onVolumeChanged,
    required this.onDelete,
  });

  final SoundEntryDto sound;
  final void Function(int value) onVolumeChanged;
  final VoidCallback onDelete;

  @override
  State<SoundEditorSheet> createState() => _SoundEditorSheetState();
}

class _SoundEditorSheetState extends State<SoundEditorSheet> {
  late double _localVolume;
  Timer? _debounce;

  @override
  void initState() {
    super.initState();
    _localVolume = widget.sound.volume.toDouble();
  }

  @override
  void dispose() {
    _debounce?.cancel();
    super.dispose();
  }

  void _onChanged(double v) {
    setState(() => _localVolume = v);
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 150), () {
      _debounce = null;
      widget.onVolumeChanged(v.round());
    });
  }

  void _onChangeEnd(double v) {
    _debounce?.cancel();
    _debounce = null;
    widget.onVolumeChanged(v.round());
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: EdgeInsets.fromLTRB(
        20,
        16,
        20,
        16 + MediaQuery.of(context).padding.bottom,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Center(
            child: Container(
              width: 40,
              height: 4,
              margin: const EdgeInsets.only(bottom: 16),
              decoration: BoxDecoration(
                color: AppColors.border,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          Text(
            widget.sound.label,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(
              color: Colors.white,
              fontSize: 18,
              fontWeight: FontWeight.w700,
            ),
          ),
          const SizedBox(height: 16),
          const Text('Volume',
              style: TextStyle(color: AppColors.muted, fontSize: 12)),
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
                  style: const TextStyle(
                      color: AppColors.muted, fontWeight: FontWeight.w600),
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          OutlinedButton.icon(
            onPressed: widget.onDelete,
            icon: const Icon(Icons.delete_outline, color: AppColors.danger),
            label: const Text('Apagar',
                style: TextStyle(color: AppColors.danger)),
            style: OutlinedButton.styleFrom(
              side: const BorderSide(color: AppColors.danger),
              padding: const EdgeInsets.symmetric(vertical: 12),
            ),
          ),
        ],
      ),
    );
  }
}
