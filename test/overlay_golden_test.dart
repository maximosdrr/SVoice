import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:svoice/app_controller.dart';
import 'package:svoice/overlay_screen.dart';

void main() {
  testWidgets('renders the initial overlay without visual regressions', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(560, 380);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final controller = SVoiceController()..status = VoiceStatus.ready;

    await tester.pumpWidget(
      MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: ThemeData(
          brightness: Brightness.dark,
          fontFamily: 'Segoe UI',
          scaffoldBackgroundColor: Colors.transparent,
          colorScheme: const ColorScheme.dark(
            primary: Color(0xFF8BE9D2),
            secondary: Color(0xFFAAB8FF),
            surface: Color(0xFF10151D),
          ),
        ),
        home: OverlayScreen(
          controller: controller,
          enableDesktopFeatures: false,
          manageControllerLifecycle: false,
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('SVOICE'), findsOneWidget);
    expect(find.text('Conectar ao Discord'), findsOneWidget);
    final layoutException = tester.takeException();
    if (layoutException is FlutterError) {
      fail(layoutException.toStringDeep());
    }
    expect(layoutException, isNull);
    await expectLater(
      find.byType(OverlayScreen),
      matchesGoldenFile('goldens/overlay_initial.png'),
    );
  });

  testWidgets('settings show active echo mode for the virtual cable', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(560, 610);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    final controller = SVoiceController()
      ..status = VoiceStatus.ready
      ..audioDevices = const [
        AudioDeviceOption(id: '', name: 'Padrão do Windows'),
        AudioDeviceOption(
          id: 'cable-id',
          name: 'CABLE Input',
          isVirtualCable: true,
        ),
      ]
      ..selectedAudioDevice = const AudioDeviceOption(
        id: 'cable-id',
        name: 'CABLE Input',
        isVirtualCable: true,
      )
      ..echoEnabled = true;

    await tester.pumpWidget(
      MaterialApp(
        theme: ThemeData(
          brightness: Brightness.dark,
          fontFamily: 'Segoe UI',
          scaffoldBackgroundColor: Colors.transparent,
          colorScheme: const ColorScheme.dark(
            primary: Color(0xFF8BE9D2),
            secondary: Color(0xFFAAB8FF),
            surface: Color(0xFF10151D),
          ),
        ),
        home: OverlayScreen(
          controller: controller,
          enableDesktopFeatures: false,
          manageControllerLifecycle: false,
        ),
      ),
    );
    expect(tester.takeException(), isNull);
    await tester.tap(find.byTooltip('Ajustes'));
    await tester.pumpAndSettle();

    expect(find.text('Modo Eco'), findsOneWidget);
    expect(
      find.text('Ouvir também na saída padrão do Windows.'),
      findsOneWidget,
    );
    final switches = tester.widgetList<Switch>(find.byType(Switch)).toList();
    expect(switches.first.value, isTrue);
    expect(switches.first.onChanged, isNotNull);
    await expectLater(
      find.byType(OverlayScreen),
      matchesGoldenFile('goldens/settings_echo.png'),
    );
    final layoutException = tester.takeException();
    if (layoutException is FlutterError) {
      fail(layoutException.toStringDeep());
    }
    expect(layoutException, isNull);
  });
}
