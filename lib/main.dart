import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:hotkey_manager/hotkey_manager.dart';
import 'package:window_manager/window_manager.dart';

import 'app_controller.dart';
import 'overlay_screen.dart';

Future<void> main(List<String> arguments) async {
  WidgetsFlutterBinding.ensureInitialized();

  await windowManager.ensureInitialized();
  await hotKeyManager.unregisterAll();

  const options = WindowOptions(
    size: Size(560, 380),
    minimumSize: Size(480, 132),
    center: true,
    backgroundColor: Colors.transparent,
    skipTaskbar: false,
    alwaysOnTop: true,
    title: 'SVoice',
    titleBarStyle: TitleBarStyle.hidden,
    windowButtonVisibility: false,
  );

  windowManager.waitUntilReadyToShow(options, () async {
    await windowManager.show();
    await windowManager.focus();
  });

  final controller = SVoiceController();
  await controller.initialize();

  final captureArgument = arguments
      .where((argument) => argument.startsWith('--capture-preview='))
      .firstOrNull;
  final capturePath = captureArgument?.substring('--capture-preview='.length);
  final previewKey = capturePath == null ? null : GlobalKey();

  runApp(SVoiceApp(controller: controller, previewKey: previewKey));

  if (capturePath != null && previewKey != null) {
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      await Future<void>.delayed(const Duration(milliseconds: 600));
      final boundary =
          previewKey.currentContext?.findRenderObject()
              as RenderRepaintBoundary?;
      if (boundary == null) return;
      final image = await boundary.toImage(pixelRatio: 1);
      final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
      if (bytes == null) return;
      final output = File(capturePath);
      await output.parent.create(recursive: true);
      await output.writeAsBytes(bytes.buffer.asUint8List());
      await windowManager.close();
    });
  }
}

class SVoiceApp extends StatelessWidget {
  const SVoiceApp({super.key, required this.controller, this.previewKey});

  final SVoiceController controller;
  final GlobalKey? previewKey;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'SVoice',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        brightness: Brightness.dark,
        fontFamily: 'Segoe UI',
        scaffoldBackgroundColor: Colors.transparent,
        colorScheme: const ColorScheme.dark(
          primary: Color(0xFF8BE9D2),
          secondary: Color(0xFFAAB8FF),
          surface: Color(0xFF10151D),
          error: Color(0xFFFF7C87),
        ),
        splashFactory: NoSplash.splashFactory,
        tooltipTheme: const TooltipThemeData(
          waitDuration: Duration(milliseconds: 450),
        ),
      ),
      home: RepaintBoundary(
        key: previewKey,
        child: OverlayScreen(controller: controller),
      ),
    );
  }
}
