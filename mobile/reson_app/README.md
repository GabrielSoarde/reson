# Reson — Android controller

Native Flutter app that controls the **Reson** PC soundboard (the WPF + ASP.NET
host living in `src/Soundpad/`). Mirrors the web controller at
`src/Soundpad/wwwroot/`: same pairing flow, same grid, same drag/drop,
same upload UX — just packaged as a real APK with offline-aware error states
and tighter haptics.

## What it does

- **Pairing.** Scan the QR Code shown on the PC sidebar (or enter IP/port/token
  by hand). Config is persisted in `shared_preferences`.
- **Grid.** Tap a sound to play, long-press to drag/swap. Live updates via
  WebSocket — what plays on the PC pulses on the phone.
- **Controls.** STOP, volume slider (debounced 100 ms to match the web UI),
  monitor (headphone passthrough) toggle.
- **Upload.** Pick `.mp3` / `.wav` / `.ogg` / `.flac` files and stream them to
  `/api/sounds/upload` one at a time.
- **Settings.** Swap pairing, pick output / monitor / mic devices.

## Build

Prereqs:

- Flutter 3.41 stable (Dart 3.11)
- Android SDK with platform 34 + build-tools, NDK as required by Flutter
- A device or emulator running Android 7.0 (API 24) or later

Build a debug APK:

```bash
cd mobile/reson_app
flutter pub get
flutter build apk --debug
```

The APK lands at:

```
mobile/reson_app/build/app/outputs/flutter-apk/app-debug.apk
```

Release build (debug-signed for now — replace before publishing):

```bash
flutter build apk --release
```

Generate launcher icons from `assets/reson_logo.png` (first build only):

```bash
dart run flutter_launcher_icons
```

## Install on a device

### Side-load over USB (fastest)

1. Enable **Developer Options → USB Debugging** on the phone.
2. Plug the phone in via USB; accept the RSA fingerprint prompt.
3. From `mobile/reson_app/`:
   ```bash
   flutter install
   ```
   Or, manually push the APK:
   ```bash
   adb install -r build/app/outputs/flutter-apk/app-debug.apk
   ```

### Side-load by file transfer

Copy `app-debug.apk` to the phone (USB / Drive / Telegram-to-self) and tap it
in the file manager. Android will ask to "Allow from this source" — confirm
once for whatever launched the install.

## Pair with the PC

1. Start Reson on the PC. The main window now shows a QR Code in the sidebar
   that encodes `http://<lan-ip>:<port>/?t=<token>`.
2. Make sure the phone is on the **same Wi-Fi network** as the PC. The app
   talks plain HTTP/WS to a LAN IP — it will not work over mobile data or a
   guest network that isolates clients.
3. Open Reson on the phone, tap **Escanear QR Code do PC**, frame the QR.
4. On success the app jumps straight to the grid.

If the camera is unavailable, tap **Inserir manualmente** and type IP/port/token
shown in the PC's Settings window.

## Troubleshooting

| Symptom | Likely cause / fix |
| --- | --- |
| "QR code inválido ou Reson offline" right after scan | Phone is on a different network (guest Wi-Fi, mobile data) or the PC firewall blocks Kestrel. Open the firewall on the chosen port (default 8080). |
| Status dot is red, banner says "Não conectado" | PC went to sleep / app closed. Wake the PC and tap **Tentar de novo**. |
| 401 banner | Token rotated on the PC. Settings → **Trocar pareamento** and scan again. |
| Camera permission denied | Settings → Apps → Reson → Permissions → Camera. Or use manual entry as a fallback. |
| File picker can't see your music folder on Android 13+ | Settings → Apps → Reson → Permissions → Music & Audio. |
| Tiles don't update when sound starts on PC | WebSocket disconnected — check the status dot. Reconnect is automatic but can take up to 30 s during backoff. |

## Project layout

```
lib/
  main.dart                  MaterialApp + initial routing
  theme.dart                 Dark theme (#0a0a0a bg, #3b82f6 accent)
  api/
    api_client.dart          HTTP client (X-Auth-Token + X-Origin-Id)
    ws_client.dart           Auto-reconnecting websocket + echo filter
    models.dart              StateDto, SoundEntryDto, GridLayout, GridPosition
  storage/
    config_store.dart        shared_preferences wrapper for pairing
  pairing/
    pairing_screen.dart      First-launch entry point
    qr_scanner_screen.dart   mobile_scanner camera view
    manual_entry_screen.dart Fallback IP/port/token form
  home/
    home_screen.dart         Main screen (grid + bottom controls + status)
    sound_grid.dart          ReorderableGridView grid renderer
    sound_tile.dart          Single colored tile (idle/playing/missing)
    bottom_controls.dart     STOP, volume, monitor toggle, upload, settings
  upload/
    upload_screen.dart       Sequential audio file uploader
  settings/
    settings_screen.dart     Devices, pairing reset, version
android/
  app/
    build.gradle.kts         applicationId com.ap2quantum.reson, minSdk 24
    src/main/AndroidManifest.xml         INTERNET, CAMERA, VIBRATE,
                                         READ_MEDIA_AUDIO
    src/main/res/xml/network_security_config.xml   cleartext for LAN
```

## Notes on cleartext HTTP

The PC server uses plain HTTP — there's no TLS. The security model is "if you
trust the Wi-Fi, you trust the traffic" (same threat surface as the QR-encoded
token itself). Android's `cleartextTrafficPermitted="true"` in
`network_security_config.xml` is required for the WebSocket to connect over
`ws://`. If you tighten this in v2 (e.g. pinning to the paired host), you'll
also need to switch the PC server to HTTPS with a trusted cert.
