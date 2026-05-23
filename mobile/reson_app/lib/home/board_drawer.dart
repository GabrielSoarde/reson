import 'package:flutter/material.dart';

import '../api/models.dart';
import '../theme.dart';

/// The 8-swatch palette used everywhere a board/sound color is chosen. Mirrors
/// the WPF palette exactly so the same color reads identically on PC and phone.
const List<String> kBoardPalette = [
  '#ef4444',
  '#f97316',
  '#eab308',
  '#22c55e',
  '#06b6d4',
  '#3b82f6',
  '#a855f7',
  '#ec4899',
];

/// Parse "#RRGGBB" / "#AARRGGBB" → Color, defaulting to accent on garbage.
Color boardHex(String s) {
  var x = s.replaceFirst('#', '');
  if (x.length == 6) x = 'FF$x';
  final n = int.tryParse(x, radix: 16);
  if (n == null) return AppColors.accent;
  return Color(n);
}

/// Navigation drawer listing the boards. The active board is highlighted with
/// its own color. Each row has a ⋮ overflow for rename / recolor / delete, and
/// a footer button to create a new board.
///
/// All actions are delegated to the parent via callbacks — the drawer holds no
/// network logic, so HomeScreen owns the optimistic-update + refetch dance.
class BoardDrawer extends StatelessWidget {
  const BoardDrawer({
    super.key,
    required this.boards,
    required this.activeBoardId,
    required this.onActivate,
    required this.onRename,
    required this.onRecolor,
    required this.onDelete,
    required this.onCreate,
  });

  final List<BoardDto> boards;
  final String? activeBoardId;
  final void Function(BoardDto board) onActivate;
  final void Function(BoardDto board) onRename;
  final void Function(BoardDto board) onRecolor;
  final void Function(BoardDto board) onDelete;
  final VoidCallback onCreate;

  @override
  Widget build(BuildContext context) {
    return Drawer(
      backgroundColor: AppColors.surface,
      child: SafeArea(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Padding(
              padding: EdgeInsets.fromLTRB(20, 20, 20, 12),
              child: Text(
                'Boards',
                style: TextStyle(
                  color: Colors.white,
                  fontSize: 20,
                  fontWeight: FontWeight.w700,
                  letterSpacing: 1,
                ),
              ),
            ),
            const Divider(height: 1, color: AppColors.border),
            Expanded(
              child: ListView.builder(
                padding: const EdgeInsets.symmetric(vertical: 8),
                itemCount: boards.length,
                itemBuilder: (context, i) {
                  final b = boards[i];
                  final active = b.id == activeBoardId;
                  final color = boardHex(b.color);
                  return Container(
                    margin: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                    decoration: BoxDecoration(
                      color: active ? color.withValues(alpha: 0.18) : null,
                      borderRadius: BorderRadius.circular(8),
                      border: active
                          ? Border.all(color: color.withValues(alpha: 0.6))
                          : null,
                    ),
                    child: ListTile(
                      onTap: () => onActivate(b),
                      leading: Container(
                        width: 14,
                        height: 14,
                        decoration: BoxDecoration(color: color, shape: BoxShape.circle),
                      ),
                      title: Text(
                        b.name,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                          color: Colors.white,
                          fontWeight: active ? FontWeight.w700 : FontWeight.w500,
                        ),
                      ),
                      trailing: PopupMenuButton<String>(
                        icon: const Icon(Icons.more_vert, color: AppColors.muted),
                        color: AppColors.surface,
                        onSelected: (v) {
                          switch (v) {
                            case 'rename':
                              onRename(b);
                              break;
                            case 'recolor':
                              onRecolor(b);
                              break;
                            case 'delete':
                              onDelete(b);
                              break;
                          }
                        },
                        itemBuilder: (context) => [
                          const PopupMenuItem(
                            value: 'rename',
                            child: Text('Renomear', style: TextStyle(color: Colors.white)),
                          ),
                          const PopupMenuItem(
                            value: 'recolor',
                            child: Text('Mudar cor', style: TextStyle(color: Colors.white)),
                          ),
                          // Hide delete when only one board remains — the
                          // backend rejects deleting the last board anyway.
                          if (boards.length > 1)
                            const PopupMenuItem(
                              value: 'delete',
                              child: Text('Apagar', style: TextStyle(color: AppColors.danger)),
                            ),
                        ],
                      ),
                    ),
                  );
                },
              ),
            ),
            const Divider(height: 1, color: AppColors.border),
            Padding(
              padding: const EdgeInsets.all(12),
              child: OutlinedButton.icon(
                onPressed: onCreate,
                icon: const Icon(Icons.add, color: AppColors.accent),
                label: const Text('Novo board', style: TextStyle(color: AppColors.accent)),
                style: OutlinedButton.styleFrom(
                  side: const BorderSide(color: AppColors.border),
                  padding: const EdgeInsets.symmetric(vertical: 12),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// A reusable 8-swatch palette grid. Highlights [selected] with a white ring.
class PaletteGrid extends StatelessWidget {
  const PaletteGrid({
    super.key,
    required this.selected,
    required this.onPick,
  });

  final String selected;
  final void Function(String color) onPick;

  @override
  Widget build(BuildContext context) {
    return Wrap(
      spacing: 12,
      runSpacing: 12,
      children: [
        for (final hex in kBoardPalette)
          GestureDetector(
            onTap: () => onPick(hex),
            child: Container(
              width: 40,
              height: 40,
              decoration: BoxDecoration(
                color: boardHex(hex),
                shape: BoxShape.circle,
                border: Border.all(
                  color: hex.toLowerCase() == selected.toLowerCase()
                      ? Colors.white
                      : Colors.transparent,
                  width: 3,
                ),
              ),
              child: hex.toLowerCase() == selected.toLowerCase()
                  ? const Icon(Icons.check, color: Colors.white, size: 20)
                  : null,
            ),
          ),
      ],
    );
  }
}
