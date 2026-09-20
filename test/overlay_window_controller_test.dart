import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:svoice/overlay_window_controller.dart';

void main() {
  Future<void> noDelay(Duration _) async {}

  test('hides a visible window that is not minimized', () async {
    final window = FakeOverlayWindow(visible: true);
    final controller = OverlayWindowController(window: window, delay: noDelay);

    final result = await controller.toggle();

    expect(result, OverlayVisibilityChange.hidden);
    expect(window.calls, ['isMinimized', 'isVisible', 'hide']);
  });

  test('shows and focuses a hidden window', () async {
    final window = FakeOverlayWindow(visible: false);
    final controller = OverlayWindowController(window: window, delay: noDelay);

    final result = await controller.toggle();

    expect(result, OverlayVisibilityChange.shown);
    expect(window.visible, isTrue);
    expect(window.focused, isTrue);
    expect(window.calls, [
      'isMinimized',
      'isVisible',
      'show',
      'isMinimized',
      'focus',
    ]);
  });

  test('restores a minimized window instead of hiding it', () async {
    final window = FakeOverlayWindow(visible: true, minimized: true);
    final controller = OverlayWindowController(window: window, delay: noDelay);

    final result = await controller.toggle();

    expect(result, OverlayVisibilityChange.shown);
    expect(window.minimized, isFalse);
    expect(window.visible, isTrue);
    expect(window.focused, isTrue);
    expect(window.calls, containsAllInOrder(['restore', 'show', 'focus']));
    expect(window.calls, isNot(contains('hide')));
  });

  test('restores when hotkey is pressed immediately after minimize', () async {
    final window = FakeOverlayWindow(
      visible: true,
      applyMinimizeImmediately: false,
    );
    final controller = OverlayWindowController(window: window, delay: noDelay);

    await controller.minimize();
    final result = await controller.toggle();

    expect(result, OverlayVisibilityChange.shown);
    expect(window.calls, containsAllInOrder(['minimize', 'restore', 'show']));
    expect(window.calls, isNot(contains('hide')));
  });

  test('ignores another toggle while a transition is running', () async {
    final visibility = Completer<bool>();
    final window = FakeOverlayWindow(
      visible: true,
      visibleResult: visibility.future,
    );
    final controller = OverlayWindowController(window: window, delay: noDelay);

    final first = controller.toggle();
    await Future<void>.delayed(Duration.zero);
    final second = await controller.toggle();
    visibility.complete(true);

    expect(second, OverlayVisibilityChange.ignored);
    expect(await first, OverlayVisibilityChange.hidden);
    expect(window.calls.where((call) => call == 'hide'), hasLength(1));
  });
}

class FakeOverlayWindow implements OverlayWindow {
  FakeOverlayWindow({
    required this.visible,
    this.minimized = false,
    this.applyMinimizeImmediately = true,
    this.visibleResult,
  });

  bool visible;
  bool minimized;
  bool focused = false;
  final bool applyMinimizeImmediately;
  final Future<bool>? visibleResult;
  final List<String> calls = [];

  @override
  Future<void> focus() async {
    calls.add('focus');
    focused = true;
  }

  @override
  Future<void> hide() async {
    calls.add('hide');
    visible = false;
  }

  @override
  Future<bool> isMinimized() async {
    calls.add('isMinimized');
    return minimized;
  }

  @override
  Future<bool> isVisible() async {
    calls.add('isVisible');
    return visibleResult == null ? visible : await visibleResult!;
  }

  @override
  Future<void> minimize() async {
    calls.add('minimize');
    if (applyMinimizeImmediately) minimized = true;
  }

  @override
  Future<void> restore() async {
    calls.add('restore');
    minimized = false;
  }

  @override
  Future<void> show() async {
    calls.add('show');
    visible = true;
  }
}
