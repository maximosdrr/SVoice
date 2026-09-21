import 'dart:async';

import 'package:window_manager/window_manager.dart';

enum OverlayVisibilityChange { shown, hidden, ignored }

abstract interface class OverlayWindow {
  Future<bool> isVisible();

  Future<bool> isMinimized();

  Future<void> show();

  Future<void> hide();

  Future<void> minimize();

  Future<void> restore();

  Future<void> focus();
}

class WindowManagerOverlayWindow implements OverlayWindow {
  const WindowManagerOverlayWindow();

  @override
  Future<void> focus() => windowManager.focus();

  @override
  Future<void> hide() => windowManager.hide();

  @override
  Future<bool> isMinimized() => windowManager.isMinimized();

  @override
  Future<bool> isVisible() => windowManager.isVisible();

  @override
  Future<void> minimize() => windowManager.minimize();

  @override
  Future<void> restore() => windowManager.restore();

  @override
  Future<void> show() => windowManager.show();
}

typedef OverlayWindowDelay = Future<void> Function(Duration duration);

Future<void> _defaultDelay(Duration duration) => Future<void>.delayed(duration);

class OverlayWindowController {
  OverlayWindowController({
    required this._window,
    OverlayWindowDelay? delay,
    this.restoreAttempts = 6,
    this.restoreInterval = const Duration(milliseconds: 35),
  }) : _delay = delay ?? _defaultDelay;

  final OverlayWindow _window;
  final OverlayWindowDelay _delay;
  final int restoreAttempts;
  final Duration restoreInterval;

  bool _transitioning = false;
  bool _minimizeRequested = false;

  void notifyMinimized() => _minimizeRequested = true;

  void notifyRestored() => _minimizeRequested = false;

  Future<void> hide() async {
    if (_transitioning) return;
    _transitioning = true;
    try {
      _minimizeRequested = false;
      await _window.hide();
    } finally {
      _transitioning = false;
    }
  }

  Future<void> minimize() async {
    _minimizeRequested = true;
    try {
      await _window.minimize();
    } catch (_) {
      _minimizeRequested = false;
      rethrow;
    }
  }

  Future<OverlayVisibilityChange> toggle() async {
    if (_transitioning) return OverlayVisibilityChange.ignored;
    _transitioning = true;
    try {
      final minimized = _minimizeRequested || await _window.isMinimized();
      if (minimized) {
        await _reveal(restoreFirst: true);
        return OverlayVisibilityChange.shown;
      }

      if (await _window.isVisible()) {
        await _window.hide();
        return OverlayVisibilityChange.hidden;
      }

      await _reveal();
      return OverlayVisibilityChange.shown;
    } finally {
      _transitioning = false;
    }
  }

  Future<void> _reveal({bool restoreFirst = false}) async {
    if (restoreFirst) {
      await _restoreUntilReady();
    }

    await _window.show();

    // window_manager restaura a janela do Windows por uma mensagem assíncrona.
    // Confirme o estado antes de focar para não deixar o SVoice preso na barra.
    if (await _window.isMinimized()) {
      await _restoreUntilReady();
      await _window.show();
    }

    _minimizeRequested = false;
    await _window.focus();
  }

  Future<void> _restoreUntilReady() async {
    for (var attempt = 0; attempt < restoreAttempts; attempt++) {
      await _window.restore();
      await _delay(restoreInterval);
      if (!await _window.isMinimized()) return;
    }
  }
}
