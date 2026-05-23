import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

/// Dark theme matching the web UI:
/// - background #0a0a0a
/// - accent (primary) #3b82f6
/// - danger #b91c1c
/// - success #16a34a
/// Letter-spaced uppercase title style for the "RESON" wordmark.
class AppColors {
  static const background = Color(0xFF0A0A0A);
  static const surface = Color(0xFF141414);
  static const border = Color(0xFF222222);
  static const accent = Color(0xFF3B82F6);
  static const danger = Color(0xFFB91C1C);
  static const success = Color(0xFF16A34A);
  static const muted = Color(0xFF888888);
  static const emptyCell = Color(0xFF2A2A2A);

  /// Status dot when WS connected
  static const online = Color(0xFF22C55E);

  /// Status dot when WS disconnected
  static const offline = Color(0xFFEF4444);
}

ThemeData buildAppTheme() {
  const seed = AppColors.accent;
  final scheme = ColorScheme.fromSeed(
    seedColor: seed,
    brightness: Brightness.dark,
    surface: AppColors.background,
  ).copyWith(
    primary: AppColors.accent,
    error: AppColors.danger,
  );
  return ThemeData(
    useMaterial3: true,
    brightness: Brightness.dark,
    colorScheme: scheme,
    scaffoldBackgroundColor: AppColors.background,
    canvasColor: AppColors.background,
    fontFamily: null,
    appBarTheme: const AppBarTheme(
      backgroundColor: AppColors.background,
      foregroundColor: Colors.white,
      elevation: 0,
      centerTitle: false,
      systemOverlayStyle: SystemUiOverlayStyle(
        statusBarColor: AppColors.background,
        statusBarIconBrightness: Brightness.light,
        statusBarBrightness: Brightness.dark,
      ),
    ),
    snackBarTheme: const SnackBarThemeData(
      backgroundColor: Color(0xFF1A1A1A),
      contentTextStyle: TextStyle(color: Colors.white, fontSize: 13.5, fontWeight: FontWeight.w600),
      behavior: SnackBarBehavior.floating,
    ),
    sliderTheme: SliderThemeData(
      activeTrackColor: AppColors.accent,
      inactiveTrackColor: const Color(0xFF333333),
      thumbColor: AppColors.accent,
      overlayColor: AppColors.accent.withValues(alpha: 0.15),
      trackHeight: 4,
    ),
    switchTheme: SwitchThemeData(
      thumbColor: WidgetStateProperty.resolveWith((s) =>
          s.contains(WidgetState.selected) ? AppColors.accent : Colors.grey.shade400),
      trackColor: WidgetStateProperty.resolveWith((s) =>
          s.contains(WidgetState.selected) ? AppColors.accent.withValues(alpha: 0.5) : Colors.grey.shade800),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: AppColors.surface,
      border: OutlineInputBorder(
        borderRadius: BorderRadius.circular(8),
        borderSide: const BorderSide(color: AppColors.border),
      ),
      enabledBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(8),
        borderSide: const BorderSide(color: AppColors.border),
      ),
      focusedBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(8),
        borderSide: const BorderSide(color: AppColors.accent, width: 2),
      ),
      labelStyle: const TextStyle(color: AppColors.muted),
    ),
    elevatedButtonTheme: ElevatedButtonThemeData(
      style: ElevatedButton.styleFrom(
        backgroundColor: AppColors.accent,
        foregroundColor: Colors.white,
        textStyle: const TextStyle(fontWeight: FontWeight.w700, fontSize: 16),
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 16),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(foregroundColor: AppColors.accent),
    ),
  );
}

/// Letter-spaced "RESON" wordmark used in the app bar.
const TextStyle kWordmarkStyle = TextStyle(
  fontSize: 16,
  fontWeight: FontWeight.w700,
  letterSpacing: 4.0,
  color: Colors.white,
);
