import 'dart:async';
import 'dart:convert';

import 'package:web_socket_channel/io.dart';
import 'package:web_socket_channel/status.dart' as ws_status;
import 'package:web_socket_channel/web_socket_channel.dart';

/// A single event broadcast by the backend StateHub.
///
/// Maps to WsEnvelope { type, originId?, payload? } on the wire.
class WsEvent {
  final String type;
  final String? originId;
  final Map<String, dynamic> payload;
  const WsEvent(this.type, this.originId, this.payload);
}

/// Auto-reconnecting websocket subscriber with the same echo-filter rules
/// the browser app uses (app.js): drop volume/monitor/monitorDeviceChanged
/// events whose originId matches *our* originId — they are the server echoing
/// our own POST back to us, and applying them would fight optimistic UI updates.
///
/// Backoff: 1s → doubles to 30s max, resets on a successful open. Reconnect
/// loop runs until [dispose] is called. The [events] stream stays alive
/// across reconnects (it's a broadcast stream from the wrapper, not the
/// underlying socket), so widgets can listen once at mount.
class WsClient {
  WsClient({required this.baseUrl, required this.token, required this.originId});

  final String baseUrl; // http(s)://host:port
  final String token;
  final String originId;

  final _controller = StreamController<WsEvent>.broadcast();
  final _connectionStateController = StreamController<bool>.broadcast();

  WebSocketChannel? _channel;
  StreamSubscription? _sub;
  Timer? _reconnectTimer;
  Duration _backoff = const Duration(seconds: 1);
  bool _disposed = false;
  bool _connected = false;

  Stream<WsEvent> get events => _controller.stream;

  /// Emits true on open, false on close/error. Used to drive the connection
  /// status dot in the header.
  Stream<bool> get connectionState => _connectionStateController.stream;

  bool get isConnected => _connected;

  void start() {
    if (_disposed) return;
    _connect();
  }

  void _connect() {
    if (_disposed) return;
    _reconnectTimer?.cancel();
    // Replace http(s):// with ws(s):// — both schemes share the same host:port.
    final wsBase = baseUrl
        .replaceFirst(RegExp('^http://'), 'ws://')
        .replaceFirst(RegExp('^https://'), 'wss://');
    final uri = Uri.parse('$wsBase/ws?t=$token');
    try {
      // Use IOWebSocketChannel so we control connect-timeout and inherit
      // dart:io socket behavior (cleartext is allowed by Android's
      // network_security_config; see manifest).
      _channel = IOWebSocketChannel.connect(uri,
          pingInterval: const Duration(seconds: 25),
          connectTimeout: const Duration(seconds: 10));
      _sub = _channel!.stream.listen(
        (data) {
          if (!_connected) {
            _connected = true;
            _backoff = const Duration(seconds: 1);
            if (!_connectionStateController.isClosed) {
              _connectionStateController.add(true);
            }
          }
          _onMessage(data);
        },
        onError: (_) => _onClosed(),
        onDone: _onClosed,
        cancelOnError: true,
      );
    } catch (_) {
      _onClosed();
    }
  }

  void _onMessage(dynamic data) {
    if (data is! String) return;
    try {
      final m = jsonDecode(data);
      if (m is! Map) return;
      final type = m['type'] as String?;
      if (type == null) return;
      final oid = m['originId'] as String?;
      final payload = (m['payload'] as Map?)?.cast<String, dynamic>() ?? const {};

      // Echo filter — drop our own broadcasts for the three event types the
      // backend stamps with the caller's X-Origin-Id (web app does the same).
      const echoFiltered = {'volumeChanged', 'monitorChanged', 'monitorDeviceChanged'};
      if (oid != null && oid == originId && echoFiltered.contains(type)) return;

      if (!_controller.isClosed) {
        _controller.add(WsEvent(type, oid, payload));
      }
    } catch (_) {
      // Malformed frame; ignore.
    }
  }

  void _onClosed() {
    if (_connected) {
      _connected = false;
      if (!_connectionStateController.isClosed) {
        _connectionStateController.add(false);
      }
    }
    _sub?.cancel();
    _sub = null;
    try {
      _channel?.sink.close(ws_status.normalClosure);
    } catch (_) {}
    _channel = null;
    if (_disposed) return;
    _reconnectTimer = Timer(_backoff, _connect);
    final next = _backoff.inMilliseconds * 2;
    _backoff = Duration(milliseconds: next.clamp(1000, 30000));
  }

  Future<void> dispose() async {
    _disposed = true;
    _reconnectTimer?.cancel();
    await _sub?.cancel();
    try {
      await _channel?.sink.close(ws_status.normalClosure);
    } catch (_) {}
    await _controller.close();
    await _connectionStateController.close();
  }
}
