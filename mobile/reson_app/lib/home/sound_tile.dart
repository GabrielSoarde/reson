import 'package:flutter/material.dart';

import '../api/models.dart';
import '../theme.dart';

/// Renders a single grid cell. Three visual variants:
/// - empty: dashed outline, "+" glyph, no interactions
/// - filled idle: colored rounded rectangle, label
/// - filled playing: white border + subtle pulse
/// - filled missing: dimmed 40% opacity
///
/// Tap behavior is wired by the parent (SoundGrid) — this widget just renders
/// and surfaces the Material ripple via InkWell. Long-press is also parent-
/// owned (reorderable_grid_view supplies its own gesture).
class SoundTile extends StatelessWidget {
  const SoundTile({
    super.key,
    required this.sound,
    required this.playing,
    this.onTap,
  });

  final SoundEntryDto sound;
  final bool playing;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final color = _hex(sound.color);
    final dim = sound.missing ? 0.4 : 1.0;
    return Opacity(
      opacity: dim,
      child: Material(
        color: color,
        borderRadius: BorderRadius.circular(8),
        clipBehavior: Clip.antiAlias,
        elevation: 2,
        shadowColor: Colors.black54,
        child: InkWell(
          onTap: onTap,
          // Splash color slightly brighter than the cell so it reads as feedback.
          splashColor: Colors.white.withValues(alpha: 0.35),
          highlightColor: Colors.white.withValues(alpha: 0.10),
          child: Container(
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(8),
              border: Border.all(
                color: playing ? Colors.white : Colors.transparent,
                width: 3,
              ),
              boxShadow: playing
                  ? [
                      BoxShadow(
                        color: Colors.white.withValues(alpha: 0.45),
                        blurRadius: 12,
                        spreadRadius: 1,
                      ),
                    ]
                  : null,
            ),
            child: Stack(
              fit: StackFit.expand,
              children: [
                Padding(
                  padding: const EdgeInsets.all(6),
                  child: Center(
                    child: Text(
                      sound.label,
                      maxLines: 3,
                      overflow: TextOverflow.ellipsis,
                      textAlign: TextAlign.center,
                      style: const TextStyle(
                        color: Colors.white,
                        fontWeight: FontWeight.w700,
                        fontSize: 12,
                        height: 1.15,
                      ),
                    ),
                  ),
                ),
                if (sound.missing)
                  const Positioned(
                    left: 0,
                    right: 0,
                    bottom: 4,
                    child: Center(
                      child: Text(
                        '(arquivo faltando)',
                        style: TextStyle(
                          color: Colors.white,
                          fontSize: 9,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Empty cell — dashed outline, no interaction. We draw the dashes with a
/// CustomPainter because Flutter has no native dashed-border decoration.
class EmptyTile extends StatelessWidget {
  const EmptyTile({super.key});

  @override
  Widget build(BuildContext context) {
    return CustomPaint(
      painter: _DashedBorderPainter(color: AppColors.emptyCell, radius: 8, dash: 6, gap: 4),
      child: const Center(
        child: Text(
          '+',
          style: TextStyle(
            color: Color(0xFF444444),
            fontSize: 22,
            fontWeight: FontWeight.w400,
          ),
        ),
      ),
    );
  }
}

class _DashedBorderPainter extends CustomPainter {
  _DashedBorderPainter({
    required this.color,
    required this.radius,
    required this.dash,
    required this.gap,
  });
  final Color color;
  final double radius;
  final double dash;
  final double gap;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2;
    final rrect = RRect.fromRectAndRadius(
        Offset.zero & size, Radius.circular(radius));
    final path = Path()..addRRect(rrect);
    final metrics = path.computeMetrics().toList();
    for (final m in metrics) {
      double distance = 0;
      while (distance < m.length) {
        final next = (distance + dash).clamp(0.0, m.length);
        canvas.drawPath(m.extractPath(distance, next), paint);
        distance = next + gap;
      }
    }
  }

  @override
  bool shouldRepaint(_DashedBorderPainter old) =>
      old.color != color || old.radius != radius || old.dash != dash || old.gap != gap;
}

/// Parse "#RRGGBB" or "#AARRGGBB" into a Color, defaulting to the accent blue
/// if anything looks wrong (the backend always stores #RRGGBB though).
Color _hex(String s) {
  var x = s.replaceFirst('#', '');
  if (x.length == 6) x = 'FF$x';
  final n = int.tryParse(x, radix: 16);
  if (n == null) return AppColors.accent;
  return Color(n);
}
