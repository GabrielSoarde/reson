import 'package:flutter_test/flutter_test.dart';
import 'package:reson_app/update/update_checker.dart';

void main() {
  group('compareSemver', () {
    test('equal versions', () {
      expect(compareSemver('1.0.0', '1.0.0'), 0);
      expect(compareSemver('1.0', '1.0.0'), 0); // missing component == 0
    });

    test('newer is greater', () {
      expect(compareSemver('1.0.1', '1.0.0'), greaterThan(0));
      expect(compareSemver('1.1.0', '1.0.9'), greaterThan(0));
      expect(compareSemver('2.0.0', '1.9.9'), greaterThan(0));
    });

    test('older is less', () {
      expect(compareSemver('1.0.0', '1.0.1'), lessThan(0));
    });

    test('strips leading v', () {
      expect(compareSemver('v1.0.1', '1.0.0'), greaterThan(0));
      expect(compareSemver('v1.0.0', 'v1.0.0'), 0);
    });

    test('strips +build suffix from pubspec versions', () {
      expect(compareSemver('1.0.1', '1.0.0+1'), greaterThan(0));
      expect(compareSemver('1.0.0+5', '1.0.0+1'), 0); // build is ignored
    });

    test('handles numeric multi-digit components', () {
      expect(compareSemver('1.0.10', '1.0.9'), greaterThan(0));
    });
  });
}
