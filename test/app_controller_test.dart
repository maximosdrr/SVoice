import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:svoice/app_controller.dart';
import 'package:svoice/xtts_service_client.dart';

void main() {
  test('voice option exposes a stable id and readable label', () {
    const voice = VoiceOption(
      name: 'Microsoft Francisca',
      locale: 'pt-BR',
      gender: 'female',
    );

    expect(voice.id, 'Microsoft Francisca|pt-BR');
    expect(voice.label, 'Microsoft Francisca  ·  pt-BR');
    expect(voice.ttsValue, {'name': 'Microsoft Francisca', 'locale': 'pt-BR'});
  });

  test('cloned voice keeps its profile id separate from Windows voices', () {
    const voice = VoiceOption(
      name: 'Minha voz',
      locale: 'pt-BR',
      gender: 'cloned',
      engine: VoiceEngine.xtts,
      profileId: 'profile-123',
    );

    expect(voice.isCloned, isTrue);
    expect(voice.id, 'xtts:profile-123');
    expect(voice.label, 'Minha voz  ·  Clonada');
  });

  test('XTTS compute mode defaults to automatic for unknown preferences', () {
    expect(
      XtssComputeMode.fromWireName('unsupported'),
      XtssComputeMode.automatic,
    );
    expect(XtssComputeMode.fromWireName('cpu'), XtssComputeMode.cpu);
    expect(XtssComputeMode.fromWireName('gpu'), XtssComputeMode.gpu);
  });

  test('cloned profile exposes processed chunks and truncation', () {
    final profile = ClonedVoiceProfile.fromJson({
      'id': 'profile-long',
      'name': 'Áudio longo',
      'reference_count': 90,
      'source_count': 3,
      'duration_seconds': 1800,
      'truncated': true,
    });

    expect(profile.referenceCount, 90);
    expect(profile.sourceCount, 3);
    expect(profile.durationSeconds, 1800);
    expect(profile.wasTruncated, isTrue);
  });

  group('voice reference validation', () {
    late Directory directory;

    setUp(() {
      directory = Directory.systemTemp.createTempSync('svoice-validation-');
    });

    tearDown(() {
      if (directory.existsSync()) directory.deleteSync(recursive: true);
    });

    test('accepts supported files and removes duplicates', () {
      final reference = File(
        '${directory.path}${Platform.pathSeparator}voz.MP3',
      )..writeAsBytesSync([1, 2, 3]);

      final paths = validateVoiceReferencePaths([
        reference.path,
        reference.path,
      ]);

      expect(paths, [reference.absolute.path]);
    });

    test('rejects an empty selection', () {
      expect(
        () => validateVoiceReferencePaths(const []),
        throwsA(
          isA<XtssServiceException>().having(
            (error) => error.message,
            'message',
            contains('pelo menos um áudio'),
          ),
        ),
      );
    });

    test('rejects missing, empty, and unsupported files', () {
      final empty = File('${directory.path}${Platform.pathSeparator}vazio.wav')
        ..createSync();
      final unsupported = File(
        '${directory.path}${Platform.pathSeparator}texto.txt',
      )..writeAsStringSync('conteúdo');

      for (final path in [
        '${directory.path}${Platform.pathSeparator}ausente.wav',
        empty.path,
        unsupported.path,
      ]) {
        expect(
          () => validateVoiceReferencePaths([path]),
          throwsA(isA<XtssServiceException>()),
          reason: path,
        );
      }
    });
  });
}
