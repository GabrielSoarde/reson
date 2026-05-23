import 'package:package_info_plus/package_info_plus.dart';

/// Reads the running app's semantic version (without the `+build` suffix)
/// from the platform package info. pubspec `version: 1.0.0+1` →
/// PackageInfo.version == "1.0.0" (the build number is exposed separately
/// as buildNumber), so this returns "1.0.0".
Future<String> currentAppVersion() async {
  final info = await PackageInfo.fromPlatform();
  return info.version;
}
