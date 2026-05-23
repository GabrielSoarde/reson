import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;

import 'version.dart';

/// Result of a successful update check: a newer release is available.
class UpdateInfo {
  const UpdateInfo({
    required this.version,
    required this.apkUrl,
    required this.notes,
  });

  /// Latest version, e.g. "1.0.1" (the leading "v" from the tag is stripped).
  final String version;

  /// Direct download URL of the `Reson-Android.apk` release asset.
  final String apkUrl;

  /// Release notes (GitHub release `body`), possibly empty.
  final String notes;
}

/// Checks GitHub Releases for a newer Reson APK. Never throws — any failure
/// (offline, 404 when no releases exist yet, malformed JSON, missing asset)
/// resolves to `null` so callers can fire-and-forget.
class UpdateChecker {
  UpdateChecker({http.Client? client, String? currentVersion})
      : _client = client ?? http.Client(),
        _currentVersionOverride = currentVersion;

  static const _latestReleaseUrl =
      'https://api.github.com/repos/GabrielSoarde/reson/releases/latest';
  static const _apkAssetName = 'Reson-Android.apk';
  static const _timeout = Duration(seconds: 12);

  final http.Client _client;

  /// Lets tests inject the "current" version; otherwise read from package info.
  final String? _currentVersionOverride;

  Future<UpdateInfo?> check() async {
    try {
      final current = _currentVersionOverride ?? await currentAppVersion();
      final r = await _client
          .get(
            Uri.parse(_latestReleaseUrl),
            headers: const {'Accept': 'application/vnd.github+json'},
          )
          .timeout(_timeout);

      // 404 == repo has no published releases yet → silently no update.
      if (r.statusCode != 200) return null;

      final json = jsonDecode(r.body);
      if (json is! Map<String, dynamic>) return null;

      final tag = (json['tag_name'] as String?)?.trim();
      if (tag == null || tag.isEmpty) return null;
      final latest = _stripV(tag);

      // Only offer an update when the release is strictly newer.
      if (compareSemver(latest, current) <= 0) return null;

      final apkUrl = _findApkUrl(json['assets']);
      if (apkUrl == null) return null;

      final notes = (json['body'] as String?)?.trim() ?? '';
      return UpdateInfo(version: latest, apkUrl: apkUrl, notes: notes);
    } catch (_) {
      // Network down, JSON parse failure, etc. — treat as "no update".
      return null;
    }
  }

  void close() => _client.close();

  static String? _findApkUrl(Object? assets) {
    if (assets is! List) return null;
    for (final a in assets) {
      if (a is Map<String, dynamic> &&
          a['name'] == _apkAssetName &&
          a['browser_download_url'] is String) {
        return a['browser_download_url'] as String;
      }
    }
    return null;
  }
}

/// Strips a single leading "v"/"V" from a tag ("v1.0.1" → "1.0.1").
String _stripV(String tag) {
  if (tag.isNotEmpty && (tag[0] == 'v' || tag[0] == 'V')) {
    return tag.substring(1);
  }
  return tag;
}

/// Compares two dotted version strings numerically.
///
/// Tolerant of input quirks: any `+build` suffix is dropped, a leading "v"
/// is ignored, missing components are treated as 0 ("1.0" == "1.0.0"), and
/// non-numeric segments fall back to 0. Returns <0 / 0 / >0 like [Comparable].
int compareSemver(String a, String b) {
  final pa = _parse(a);
  final pb = _parse(b);
  final len = pa.length > pb.length ? pa.length : pb.length;
  for (var i = 0; i < len; i++) {
    final x = i < pa.length ? pa[i] : 0;
    final y = i < pb.length ? pb[i] : 0;
    if (x != y) return x < y ? -1 : 1;
  }
  return 0;
}

List<int> _parse(String v) {
  var s = _stripV(v.trim());
  // Drop the pubspec "+build" suffix and any "-prerelease" tail before splitting.
  final plus = s.indexOf('+');
  if (plus != -1) s = s.substring(0, plus);
  final dash = s.indexOf('-');
  if (dash != -1) s = s.substring(0, dash);
  return s
      .split('.')
      .map((p) => int.tryParse(p.trim()) ?? 0)
      .toList(growable: false);
}
