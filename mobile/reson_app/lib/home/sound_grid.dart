import 'package:flutter/material.dart';
import 'package:reorderable_grid_view/reorderable_grid_view.dart';
import 'package:vibration/vibration.dart';

import '../api/models.dart';
import 'sound_tile.dart';

/// Renders the cols×rows grid, mixing positioned sounds with empty placeholders.
///
/// Drag/drop behavior mirrors web app.js commitDrop():
///   - tap → onPlay(sound)
///   - long-press (~500ms via reorderable_grid_view) → drag mode
///   - drop on another cell → swap positions (or move into empty)
///   - drop in the same cell → no-op
/// The parent (HomeScreen) sends the resulting placements to /api/grid/layout.
///
/// IMPORTANT — drag suspension:
/// We deliberately do NOT rebuild the grid while a drag is in-flight. The
/// parent must hold the WS libraryChanged "deferred render" flag (same idea
/// as app.js `drag.deferredRender`) and only call setState on us when our
/// drag callback has returned. reorderable_grid_view handles that internally
/// per gesture.
class SoundGrid extends StatelessWidget {
  const SoundGrid({
    super.key,
    required this.cols,
    required this.rows,
    required this.sounds,
    required this.nowPlaying,
    required this.onPlay,
    required this.onLayoutChanged,
    this.editing = false,
    this.onEdit,
  });

  final int cols;
  final int rows;
  final List<SoundEntryDto> sounds;
  final String? nowPlaying;
  final void Function(SoundEntryDto sound) onPlay;

  /// When true, tapping a tile opens the editor sheet instead of playing.
  /// Long-press still drags. The pencil affordance is rendered on each tile.
  final bool editing;

  /// Tap handler used while [editing]. Required when editing is true.
  final void Function(SoundEntryDto sound)? onEdit;

  /// Called when a successful drop produced a new full layout.
  /// The list contains EVERY positioned sound after the move, exactly the
  /// shape /api/grid/layout expects.
  final void Function(List<LayoutPlacement> placements) onLayoutChanged;

  @override
  Widget build(BuildContext context) {
    // Build a cell list of length cols*rows. Each slot is either a sound
    // (matched by position) or null (empty). Items in reorderable_grid_view
    // need stable keys — sounds use their id, empties use the slot index
    // prefixed with "empty:".
    final byPos = <String, SoundEntryDto>{
      for (final s in sounds)
        if (s.position != null) '${s.position!.col},${s.position!.row}': s
    };
    final cells = <_Cell>[];
    for (var r = 0; r < rows; r++) {
      for (var c = 0; c < cols; c++) {
        final key = '$c,$r';
        final sound = byPos[key];
        cells.add(_Cell(col: c, row: r, sound: sound));
      }
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        final w = constraints.maxWidth;
        // Cell sizing copies the web CSS: 10px gap, max 110px, equal aspect.
        const gap = 10.0;
        final cellSize = ((w - gap * (cols - 1)) / cols).clamp(60.0, 140.0);
        final aspect = 1.0;

        return SingleChildScrollView(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: SizedBox(
              width: cellSize * cols + gap * (cols - 1),
              child: ReorderableGridView.builder(
                shrinkWrap: true,
                physics: const NeverScrollableScrollPhysics(),
                itemCount: cells.length,
                gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
                  crossAxisCount: cols,
                  mainAxisSpacing: gap,
                  crossAxisSpacing: gap,
                  childAspectRatio: aspect,
                ),
                onReorder: (oldIndex, newIndex) async {
                  await _onReorder(cells, oldIndex, newIndex);
                },
                // The reorderable_grid_view default drag-start is a long-press
                // which is exactly what we want — matches the web UX of
                // ~350ms hold to enter drag mode.
                itemBuilder: (context, index) {
                  final cell = cells[index];
                  final keyStr = cell.sound != null
                      ? 'sound:${cell.sound!.id}'
                      : 'empty:${cell.col},${cell.row}';
                  if (cell.sound == null) {
                    return KeyedSubtree(
                      key: ValueKey(keyStr),
                      child: const EmptyTile(),
                    );
                  }
                  return KeyedSubtree(
                    key: ValueKey(keyStr),
                    child: SoundTile(
                      sound: cell.sound!,
                      playing: cell.sound!.id == nowPlaying,
                      editing: editing,
                      onTap: () => editing
                          ? onEdit?.call(cell.sound!)
                          : onPlay(cell.sound!),
                    ),
                  );
                },
              ),
            ),
          ),
        );
      },
    );
  }

  Future<void> _onReorder(List<_Cell> cells, int oldIndex, int newIndex) async {
    if (oldIndex == newIndex) return;
    final src = cells[oldIndex];
    // Empty cells should not be draggable (no semantic to "move emptiness"),
    // but reorderable_grid_view doesn't expose a per-item dragEnabled flag —
    // so we silently no-op here.
    if (src.sound == null) return;
    final dst = cells[newIndex];

    // Tiny haptic to confirm the drop landed.
    final hasVibrator = await Vibration.hasVibrator() ?? false;
    if (hasVibrator) {
      Vibration.vibrate(duration: 20);
    }

    final srcPos = src.sound!.position!;
    final dstCol = dst.col;
    final dstRow = dst.row;
    if (srcPos.col == dstCol && srcPos.row == dstRow) return;

    // Build placements: every currently positioned sound, with src moved and
    // (if dst was occupied) dst swapped to src's old position. Matches the
    // web client's commitDrop() exactly.
    final placements = <LayoutPlacement>[];
    for (final s in sounds) {
      if (s.position == null) continue;
      if (s.id == src.sound!.id) {
        placements.add(LayoutPlacement(s.id, GridPosition(dstCol, dstRow)));
      } else if (dst.sound != null && s.id == dst.sound!.id) {
        placements.add(LayoutPlacement(s.id, GridPosition(srcPos.col, srcPos.row)));
      } else {
        placements.add(LayoutPlacement(s.id, s.position!));
      }
    }
    onLayoutChanged(placements);
  }
}

class _Cell {
  _Cell({required this.col, required this.row, required this.sound});
  final int col;
  final int row;
  final SoundEntryDto? sound;
}
