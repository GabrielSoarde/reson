import 'package:shared_preferences/shared_preferences.dart';

/// Thin wrapper around shared_preferences for the pairing config.
///
/// We persist just enough to skip the QR step on subsequent launches:
/// - pairedUrl: base URL of the PC, e.g. http://192.168.1.42:8080
/// - pairedToken: opaque secret from the QR's ?t= query
///
/// Token is stored in plain prefs (not Keystore) because it's a LAN-only
/// credential equivalent to a Wi-Fi password — if the device is compromised,
/// the network is too. Rotation is via "Trocar pareamento" in Settings.
class PairedConfig {
  final String url;
  final String token;
  const PairedConfig(this.url, this.token);
}

class ConfigStore {
  static const _kUrl = 'paired_url';
  static const _kToken = 'paired_token';

  Future<PairedConfig?> load() async {
    final prefs = await SharedPreferences.getInstance();
    final url = prefs.getString(_kUrl);
    final token = prefs.getString(_kToken);
    if (url == null || token == null || url.isEmpty || token.isEmpty) return null;
    return PairedConfig(url, token);
  }

  Future<void> save(PairedConfig cfg) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_kUrl, cfg.url);
    await prefs.setString(_kToken, cfg.token);
  }

  Future<void> clear() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.remove(_kUrl);
    await prefs.remove(_kToken);
  }
}
