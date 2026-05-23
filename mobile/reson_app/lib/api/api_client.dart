import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:http/http.dart' as http;
import 'package:uuid/uuid.dart';

import 'models.dart';

/// Thin HTTP wrapper around the PC Soundpad backend.
///
/// Mirrors src/Soundpad/wwwroot/app.js behaviors:
/// - X-Auth-Token on every request (HTTP) — backend rejects with 401 otherwise.
/// - X-Origin-Id on every mutating POST so the WS echo filter can ignore our
///   own broadcasts (volume/monitor flicker).
/// - Sensible per-request timeout — LAN should be fast; long hangs almost
///   always mean the PC server died/rebooted and the WS reconnect loop hasn't
///   tripped yet.
class ApiClient {
  ApiClient({required this.baseUrl, required this.token, String? originId})
      : originId = originId ?? const Uuid().v4();

  final String baseUrl; // e.g. http://192.168.1.42:8080
  final String token;
  final String originId;

  static const _timeout = Duration(seconds: 8);
  final _http = http.Client();

  Map<String, String> _jsonHeaders() => {
        'X-Auth-Token': token,
        'X-Origin-Id': originId,
        'Content-Type': 'application/json',
      };

  Map<String, String> _bareHeaders() => {
        'X-Auth-Token': token,
        'X-Origin-Id': originId,
      };

  Uri _u(String path) => Uri.parse('$baseUrl$path');

  /// GET /api/state — refreshes everything. Used on startup, after libraryChanged,
  /// and as a reachability probe during pairing.
  Future<StateDto> fetchState() async {
    final r = await _http.get(_u('/api/state'), headers: _bareHeaders()).timeout(_timeout);
    if (r.statusCode == 401) throw ApiException(r.statusCode, 'unauthorized');
    if (r.statusCode != 200) throw ApiException(r.statusCode, r.body);
    return StateDto.fromJson(jsonDecode(r.body) as Map<String, dynamic>);
  }

  Future<void> play(String soundId) async {
    final r = await _http
        .post(_u('/api/play/$soundId'), headers: _jsonHeaders())
        .timeout(_timeout);
    if (r.statusCode != 204 && r.statusCode != 404) {
      throw ApiException(r.statusCode, r.body);
    }
  }

  Future<void> stop() async {
    final r = await _http.post(_u('/api/stop'), headers: _jsonHeaders()).timeout(_timeout);
    _expect204(r);
  }

  Future<void> setVolume(int value) async {
    final r = await _http
        .post(_u('/api/volume'),
            headers: _jsonHeaders(), body: jsonEncode({'value': value}))
        .timeout(_timeout);
    _expect204(r);
  }

  Future<void> setMonitor(bool enabled) async {
    final r = await _http
        .post(_u('/api/monitor'),
            headers: _jsonHeaders(), body: jsonEncode({'enabled': enabled}))
        .timeout(_timeout);
    _expect204(r);
  }

  /// Apply the full grid layout. The web UI sends a snapshot of all positioned
  /// sounds and the backend reconciles by id, so we just hand it a list of
  /// (id, position) pairs.
  Future<void> applyLayout(List<LayoutPlacement> placements) async {
    final r = await _http
        .post(_u('/api/grid/layout'),
            headers: _jsonHeaders(),
            body: jsonEncode({'placements': placements.map((p) => p.toJson()).toList()}))
        .timeout(_timeout);
    _expect204(r);
  }

  Future<void> setGameDevice(String? device) async {
    final r = await _http
        .post(_u('/api/game/device'),
            headers: _jsonHeaders(), body: jsonEncode({'device': device}))
        .timeout(_timeout);
    _expect204(r);
  }

  Future<void> setMonitorDevice(String? device) async {
    final r = await _http
        .post(_u('/api/monitor/device'),
            headers: _jsonHeaders(), body: jsonEncode({'device': device}))
        .timeout(_timeout);
    _expect204(r);
  }

  Future<void> setMicDevice(String? device) async {
    final r = await _http
        .post(_u('/api/mic/device'),
            headers: _jsonHeaders(), body: jsonEncode({'device': device}))
        .timeout(_timeout);
    _expect204(r);
  }

  /// Upload a single audio file. Returns the parsed entry on 201.
  ///
  /// The browser path uses multipart/form-data and so do we — package:http's
  /// MultipartRequest sets the boundary header for us, mirroring fetch()'s
  /// behavior. Upload timeout is generous (30s) since some phones are slow at
  /// reading large files from external storage.
  Future<SoundEntryDto> uploadSound(String filePath, String filename) async {
    final uri = _u('/api/sounds/upload');
    final req = http.MultipartRequest('POST', uri);
    req.headers.addAll(_bareHeaders());
    req.files.add(await http.MultipartFile.fromPath('file', filePath, filename: filename));
    final streamed = await _http.send(req).timeout(const Duration(seconds: 60));
    final body = await streamed.stream.bytesToString();
    if (streamed.statusCode == 201) {
      return SoundEntryDto.fromJson(jsonDecode(body) as Map<String, dynamic>);
    }
    // Surface backend "error" field if present so the UI can show it.
    String detail = body;
    try {
      final j = jsonDecode(body);
      if (j is Map && j['error'] is String) detail = j['error'] as String;
    } catch (_) {}
    throw ApiException(streamed.statusCode, detail);
  }

  // --- boards ----------------------------------------------------------------

  /// Switch the active board. Backend broadcasts WS activeBoardChanged.
  Future<void> activateBoard(String id) async {
    final r = await _http
        .post(_u('/api/boards/$id/activate'), headers: _jsonHeaders())
        .timeout(_timeout);
    _expect204(r);
  }

  /// Create a board. Backend broadcasts WS boardsChanged.
  Future<void> createBoard(String name, String color) async {
    final r = await _http
        .post(_u('/api/boards'),
            headers: _jsonHeaders(),
            body: jsonEncode({'name': name, 'color': color}))
        .timeout(_timeout);
    // 201 Created or 200 are both acceptable success codes.
    if (r.statusCode != 201 && r.statusCode != 200 && r.statusCode != 204) {
      throw ApiException(r.statusCode, r.body);
    }
  }

  /// Rename and/or recolor a board. Only sends the fields provided.
  Future<void> renameBoard(String id, {String? name, String? color}) async {
    final body = <String, dynamic>{};
    if (name != null) body['name'] = name;
    if (color != null) body['color'] = color;
    final r = await _http
        .put(_u('/api/boards/$id'),
            headers: _jsonHeaders(), body: jsonEncode(body))
        .timeout(_timeout);
    if (r.statusCode != 204 && r.statusCode != 200) {
      throw ApiException(r.statusCode, r.body);
    }
  }

  /// Delete a board. Backend returns 400 if it's the last remaining board.
  Future<void> deleteBoard(String id) async {
    final r = await _http
        .delete(_u('/api/boards/$id'), headers: _bareHeaders())
        .timeout(_timeout);
    if (r.statusCode != 204 && r.statusCode != 200) {
      throw ApiException(r.statusCode, r.body);
    }
  }

  // --- per-sound -------------------------------------------------------------

  /// Set a single sound's gain (0-100). Note: POST, not PUT.
  Future<void> setSoundVolume(String soundId, int value) async {
    final r = await _http
        .post(_u('/api/sounds/$soundId/volume'),
            headers: _jsonHeaders(), body: jsonEncode({'value': value}))
        .timeout(_timeout);
    _expect204(r);
  }

  /// Delete a sound and its file from disk.
  Future<void> deleteSound(String id) async {
    final r = await _http
        .delete(_u('/api/sounds/$id?deleteFile=true'), headers: _bareHeaders())
        .timeout(_timeout);
    if (r.statusCode != 204 && r.statusCode != 200 && r.statusCode != 404) {
      throw ApiException(r.statusCode, r.body);
    }
  }

  void _expect204(http.Response r) {
    if (r.statusCode != 204) throw ApiException(r.statusCode, r.body);
  }

  void close() => _http.close();
}

class ApiException implements Exception {
  final int statusCode;
  final String body;
  ApiException(this.statusCode, this.body);
  @override
  String toString() => 'ApiException($statusCode): $body';
}

/// Convenience: returns true iff GET /api/state returns 200 with a valid body.
/// Used as the pairing reachability probe. Catches every error type because
/// pairing has many failure modes (no DNS, refused, TLS mismatch, timeouts).
Future<bool> probeConnection(String baseUrl, String token) async {
  try {
    final c = ApiClient(baseUrl: baseUrl, token: token);
    try {
      await c.fetchState();
      return true;
    } finally {
      c.close();
    }
  } on ApiException {
    return false;
  } on SocketException {
    return false;
  } on TimeoutException {
    return false;
  } catch (_) {
    return false;
  }
}
