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
}
