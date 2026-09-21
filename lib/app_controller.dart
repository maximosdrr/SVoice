import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter_tts/flutter_tts.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'xtts_service_client.dart';

enum VoiceStatus { loading, ready, generating, speaking, error }

enum VoiceEngine { windows, xtts }

@immutable
class VoiceOption {
  const VoiceOption({
    required this.name,
    required this.locale,
    required this.gender,
    this.engine = VoiceEngine.windows,
    this.profileId,
  });

  final String name;
  final String locale;
  final String gender;
  final VoiceEngine engine;
  final String? profileId;

  bool get isCloned => engine == VoiceEngine.xtts;
  String get id => isCloned ? 'xtts:$profileId' : '$name|$locale';
  String get label => isCloned ? '$name  ·  Clonada' : '$name  ·  $locale';

  Map<String, String> get ttsValue => {'name': name, 'locale': locale};
}

@immutable
class AudioDeviceOption {
  const AudioDeviceOption({
    required this.id,
    required this.name,
    this.isVirtualCable = false,
  });

  final String id;
  final String name;
  final bool isVirtualCable;
}

@immutable
class SpokenMessage {
  const SpokenMessage({required this.text, required this.createdAt});

  final String text;
  final DateTime createdAt;
}

class SVoiceController extends ChangeNotifier {
  SVoiceController({FlutterTts? tts, XtssServiceClient? xttsClient})
    : _tts = tts ?? FlutterTts(),
      _xtts = xttsClient ?? XtssServiceClient();

  static const maxHistoryLength = 6;

  final FlutterTts _tts;
  final XtssServiceClient _xtts;
  SharedPreferences? _preferences;
  bool _disposed = false;
  Future<void>? _shutdownFuture;
  List<VoiceOption> _windowsVoices = const [];
  Timer? _runtimePollTimer;
  int _synthesisTicket = 0;
  String? _pendingClonedAudioPath;

  VoiceStatus status = VoiceStatus.loading;
  List<VoiceOption> voices = const [];
  VoiceOption? selectedVoice;
  List<AudioDeviceOption> audioDevices = const [];
  AudioDeviceOption? selectedAudioDevice;
  List<SpokenMessage> history = const [];
  List<ClonedVoiceProfile> clonedVoiceProfiles = const [];
  XtssRuntimeInfo? cloningRuntimeInfo;
  XtssComputeMode cloningComputeMode = XtssComputeMode.automatic;

  double volume = 1;
  double rate = .5;
  double pitch = 1;
  double panelOpacity = .72;
  bool clearAfterSpeaking = true;
  bool echoEnabled = false;
  String? errorMessage;
  String? cloningError;
  String? cloningStatusMessage;

  bool get isSpeaking => status == VoiceStatus.speaking;
  bool get isGenerating => status == VoiceStatus.generating;
  bool get isBusy => isSpeaking || isGenerating;
  bool get isUsingClonedVoice => selectedVoice?.isCloned ?? false;
  bool get cloningAvailable => _xtts.isReady;
  bool get hasVirtualCable =>
      audioDevices.any((device) => device.isVirtualCable);
  bool get isUsingVirtualCable => selectedAudioDevice?.isVirtualCable ?? false;
  bool get isEchoActive => echoEnabled && isUsingVirtualCable;

  String get cloningSummary {
    if (!cloningAvailable) {
      return cloningError ?? 'Mecanismo XTTS indisponível';
    }
    final runtime = cloningRuntimeInfo;
    if (runtime == null) return 'Mecanismo XTTS pronto';
    final device = switch (cloningComputeMode) {
      XtssComputeMode.cpu => 'CPU',
      XtssComputeMode.gpu =>
        runtime.hardwareChecked
            ? runtime.gpuName ?? 'GPU'
            : 'GPU (verificada na primeira fala)',
      XtssComputeMode.automatic =>
        !runtime.hardwareChecked
            ? 'Automático: GPU se disponível, com fallback para CPU'
            : runtime.cudaAvailable
            ? '${runtime.gpuName ?? 'GPU'} com fallback para CPU'
            : 'CPU',
    };
    return 'Processamento: $device';
  }

  Future<void> initialize() async {
    _preferences = await SharedPreferences.getInstance();
    volume = _preferences?.getDouble('volume') ?? 1;
    rate = _preferences?.getDouble('rate') ?? .5;
    pitch = _preferences?.getDouble('pitch') ?? 1;
    panelOpacity = _preferences?.getDouble('panelOpacity') ?? .72;
    clearAfterSpeaking = _preferences?.getBool('clearAfterSpeaking') ?? true;
    echoEnabled = _preferences?.getBool('echoEnabled') ?? false;
    cloningComputeMode = XtssComputeMode.fromWireName(
      _preferences?.getString('xttsComputeMode'),
    );

    _tts.setStartHandler(() {
      status = VoiceStatus.speaking;
      errorMessage = null;
      _notify();
    });
    _tts.setCompletionHandler(() {
      status = VoiceStatus.ready;
      _cleanupPendingClonedAudio();
      _notify();
    });
    _tts.setCancelHandler(() {
      status = VoiceStatus.ready;
      _cleanupPendingClonedAudio();
      _notify();
    });
    _tts.setErrorHandler((message) {
      status = VoiceStatus.error;
      errorMessage = message;
      _cleanupPendingClonedAudio();
      _notify();
    });

    var windowsReady = false;
    try {
      await _applySoundSettings();
      await _loadWindowsVoices();
      await _loadAudioDevices();
      windowsReady = true;
    } catch (error) {
      errorMessage = 'Não foi possível iniciar as vozes do Windows.';
      debugPrint('SVoice TTS initialization error: $error');
    }

    try {
      await _initializeCloning();
    } catch (error) {
      cloningError = _friendlyError(error);
      debugPrint('SVoice XTTS initialization error: $error');
    }

    status = windowsReady || cloningAvailable
        ? VoiceStatus.ready
        : VoiceStatus.error;
    _notify();
  }

  Future<void> _initializeCloning() async {
    await _xtts.start();
    await _xtts.setComputeMode(cloningComputeMode);
    cloningRuntimeInfo = await _xtts.getRuntimeInfo();
    clonedVoiceProfiles = await _xtts.listProfiles();
    cloningError = null;
    _rebuildVoiceList();
  }

  Future<void> _loadAudioDevices() async {
    const systemDefault = AudioDeviceOption(id: '', name: 'Padrão do Windows');
    final available = <AudioDeviceOption>[systemDefault];
    final rawDevices = await _tts.getAudioDevices;

    if (rawDevices is List) {
      for (final rawDevice in rawDevices) {
        if (rawDevice is! Map) continue;
        final id = rawDevice['id']?.toString();
        final name = rawDevice['name']?.toString();
        if (id == null || name == null) continue;
        final normalizedName = name.toLowerCase();
        available.add(
          AudioDeviceOption(
            id: id,
            name: name,
            isVirtualCable:
                normalizedName.contains('cable input') ||
                normalizedName.contains('vb-audio'),
          ),
        );
      }
    }

    final outputs = available.skip(1).toList()
      ..sort((a, b) {
        if (a.isVirtualCable != b.isVirtualCable) {
          return a.isVirtualCable ? -1 : 1;
        }
        return a.name.compareTo(b.name);
      });
    audioDevices = List.unmodifiable([systemDefault, ...outputs]);

    final storedId = _preferences?.getString('audioDeviceId');
    selectedAudioDevice =
        _findAudioDevice(storedId) ?? _firstVirtualCable() ?? systemDefault;
    await _tts.setAudioDevice(selectedAudioDevice!.id);
    await _syncEchoOutput();
  }

  AudioDeviceOption? _findAudioDevice(String? id) {
    if (id == null) return null;
    for (final device in audioDevices) {
      if (device.id == id) return device;
    }
    return null;
  }

  AudioDeviceOption? _firstVirtualCable() {
    for (final device in audioDevices) {
      if (device.isVirtualCable) return device;
    }
    return null;
  }

  Future<void> _loadWindowsVoices() async {
    final rawVoices = await _tts.getVoices;
    final available = <VoiceOption>[];

    if (rawVoices is List) {
      for (final rawVoice in rawVoices) {
        if (rawVoice is! Map) continue;
        final name = rawVoice['name']?.toString();
        final locale = rawVoice['locale']?.toString();
        if (name == null || locale == null) continue;
        available.add(
          VoiceOption(
            name: name,
            locale: locale,
            gender: rawVoice['gender']?.toString() ?? 'unknown',
          ),
        );
      }
    }

    available.sort((a, b) {
      final aPortuguese = a.locale.toLowerCase().startsWith('pt') ? 0 : 1;
      final bPortuguese = b.locale.toLowerCase().startsWith('pt') ? 0 : 1;
      final byLanguage = aPortuguese.compareTo(bPortuguese);
      return byLanguage != 0 ? byLanguage : a.label.compareTo(b.label);
    });
    _windowsVoices = List.unmodifiable(available);
    _rebuildVoiceList();
  }

  void _rebuildVoiceList() {
    final cloned = clonedVoiceProfiles
        .map(
          (profile) => VoiceOption(
            name: profile.name,
            locale: 'pt-BR',
            gender: 'cloned',
            engine: VoiceEngine.xtts,
            profileId: profile.id,
          ),
        )
        .toList(growable: false);
    voices = List.unmodifiable([...cloned, ..._windowsVoices]);

    final storedVoiceId = _preferences?.getString('voiceId');
    selectedVoice =
        _findVoice(storedVoiceId) ??
        _findVoice(selectedVoice?.id) ??
        _firstMatchingLocale('pt-BR') ??
        _firstMatchingLocale('pt') ??
        (voices.isEmpty ? null : voices.first);
  }

  VoiceOption? _findVoice(String? id) {
    if (id == null) return null;
    for (final voice in voices) {
      if (voice.id == id) return voice;
    }
    return null;
  }

  VoiceOption? _firstMatchingLocale(String locale) {
    final normalized = locale.toLowerCase();
    for (final voice in _windowsVoices) {
      if (voice.locale.toLowerCase().startsWith(normalized)) return voice;
    }
    return null;
  }

  Future<void> speak(String value, {bool addToHistory = true}) async {
    final text = value.trim();
    if (text.isEmpty) return;

    try {
      if (isBusy) await stop();
      final voice = selectedVoice;
      if (voice == null) {
        throw StateError('Nenhuma voz selecionada.');
      }

      if (addToHistory) {
        history = [
          SpokenMessage(text: text, createdAt: DateTime.now()),
          ...history.where((message) => message.text != text),
        ].take(maxHistoryLength).toList(growable: false);
      }

      if (voice.isCloned) {
        await _speakWithClonedVoice(text, voice);
      } else {
        await _speakWithWindowsVoice(text, voice);
      }
    } catch (error) {
      if (status == VoiceStatus.ready) return;
      status = VoiceStatus.error;
      errorMessage = _friendlyError(error);
      debugPrint('SVoice speak error: $error');
      _notify();
    }
  }

  Future<void> _speakWithWindowsVoice(String text, VoiceOption voice) async {
    await _applySoundSettings();
    await _tts.setVoice(voice.ttsValue);
    status = VoiceStatus.speaking;
    errorMessage = null;
    _notify();
    final result = await _tts.speak(text);
    if (result != 1) {
      throw StateError('A voz do Windows não aceitou esta mensagem.');
    }
  }

  Future<void> _speakWithClonedVoice(String text, VoiceOption voice) async {
    if (!_xtts.isReady || voice.profileId == null) {
      throw XtssServiceException(
        cloningError ?? 'O mecanismo de clonagem não está disponível.',
      );
    }

    final ticket = ++_synthesisTicket;
    status = VoiceStatus.generating;
    errorMessage = null;
    cloningStatusMessage = 'Preparando a voz clonada…';
    _startRuntimePolling();
    _notify();

    final outputPath = await _xtts.synthesize(
      text: text,
      profileId: voice.profileId!,
      speed: (rate + .5).clamp(.5, 1.5),
      computeMode: cloningComputeMode,
    );
    _stopRuntimePolling();
    if (ticket != _synthesisTicket) {
      _deleteFile(outputPath);
      return;
    }

    _pendingClonedAudioPath = outputPath;
    await _tts.setVolume(volume);
    status = VoiceStatus.speaking;
    cloningStatusMessage = null;
    _notify();
    final result = await _tts.playAudioFile(outputPath);
    if (result != 1) {
      throw StateError('O Windows não conseguiu reproduzir a voz clonada.');
    }
  }

  void _startRuntimePolling() {
    _stopRuntimePolling();
    _runtimePollTimer = Timer.periodic(const Duration(seconds: 1), (_) async {
      if (!_xtts.isReady || status != VoiceStatus.generating) return;
      try {
        cloningRuntimeInfo = await _xtts.getRuntimeInfo();
        cloningStatusMessage = cloningRuntimeInfo?.message;
        _notify();
      } catch (_) {}
    });
  }

  void _stopRuntimePolling() {
    _runtimePollTimer?.cancel();
    _runtimePollTimer = null;
  }

  Future<void> stop() async {
    _synthesisTicket++;
    _stopRuntimePolling();
    if (isGenerating) {
      status = VoiceStatus.loading;
      cloningStatusMessage = 'Interrompendo a geração…';
      _notify();
      try {
        await _xtts.cancelAndRestart();
        await _xtts.setComputeMode(cloningComputeMode);
        cloningRuntimeInfo = await _xtts.getRuntimeInfo();
        cloningError = null;
      } catch (error) {
        cloningError = _friendlyError(error);
      }
    }
    await _tts.stop();
    _cleanupPendingClonedAudio();
    cloningStatusMessage = null;
    status = VoiceStatus.ready;
    _notify();
  }

  Future<void> selectVoice(VoiceOption? voice) async {
    if (voice == null) return;
    selectedVoice = voice;
    if (!voice.isCloned) await _tts.setVoice(voice.ttsValue);
    await _preferences?.setString('voiceId', voice.id);
    _notify();
  }

  Future<ClonedVoiceProfile> addClonedVoice({
    String? name,
    required List<String> referencePaths,
  }) async {
    if (!_xtts.isReady) await _initializeCloning();
    cloningStatusMessage = 'Preparando e cortando os áudios…';
    _notify();
    try {
      final profile = await _xtts.addProfile(
        name: name,
        referencePaths: referencePaths,
      );
      clonedVoiceProfiles = await _xtts.listProfiles();
      _rebuildVoiceList();
      await selectVoice(_findVoice('xtts:${profile.id}'));
      cloningError = null;
      return profile;
    } finally {
      cloningStatusMessage = null;
      _notify();
    }
  }

  Future<void> deleteClonedVoice(String profileId) async {
    await _xtts.deleteProfile(profileId);
    clonedVoiceProfiles = await _xtts.listProfiles();
    if (selectedVoice?.profileId == profileId) {
      selectedVoice =
          _firstMatchingLocale('pt-BR') ??
          (_windowsVoices.isEmpty ? null : _windowsVoices.first);
      if (selectedVoice != null) {
        await _preferences?.setString('voiceId', selectedVoice!.id);
        await _tts.setVoice(selectedVoice!.ttsValue);
      }
    }
    _rebuildVoiceList();
    _notify();
  }

  Future<void> updateCloningComputeMode(XtssComputeMode mode) async {
    cloningComputeMode = mode;
    await _preferences?.setString('xttsComputeMode', mode.wireName);
    if (_xtts.isReady) {
      await _xtts.setComputeMode(mode);
      cloningRuntimeInfo = await _xtts.getRuntimeInfo();
    }
    _notify();
  }

  Future<void> selectAudioDevice(AudioDeviceOption? device) async {
    if (device == null) return;
    selectedAudioDevice = device;
    await _tts.setAudioDevice(device.id);
    await _syncEchoOutput();
    await _preferences?.setString('audioDeviceId', device.id);
    _notify();
  }

  void updateVolume(double value) {
    volume = value;
    _tts.setVolume(value);
    _notify();
  }

  void updateRate(double value) {
    rate = value;
    if (!isUsingClonedVoice) _tts.setSpeechRate(value);
    _notify();
  }

  void updatePitch(double value) {
    pitch = value;
    if (!isUsingClonedVoice) _tts.setPitch(value);
    _notify();
  }

  void updatePanelOpacity(double value) {
    panelOpacity = value;
    _notify();
  }

  void updateClearAfterSpeaking(bool value) {
    clearAfterSpeaking = value;
    _preferences?.setBool('clearAfterSpeaking', value);
    _notify();
  }

  Future<void> updateEchoEnabled(bool value) async {
    echoEnabled = value;
    await _syncEchoOutput();
    await _preferences?.setBool('echoEnabled', value);
    _notify();
  }

  Future<void> _syncEchoOutput() async {
    await _tts.setEchoEnabled(isEchoActive);
  }

  Future<void> saveSettings() async {
    await Future.wait([
      _preferences?.setDouble('volume', volume) ?? Future.value(true),
      _preferences?.setDouble('rate', rate) ?? Future.value(true),
      _preferences?.setDouble('pitch', pitch) ?? Future.value(true),
      _preferences?.setDouble('panelOpacity', panelOpacity) ??
          Future.value(true),
      _preferences?.setString('xttsComputeMode', cloningComputeMode.wireName) ??
          Future.value(true),
    ]);
  }

  Future<void> _applySoundSettings() async {
    await _tts.setVolume(volume);
    await _tts.setSpeechRate(rate);
    await _tts.setPitch(pitch);
  }

  void _cleanupPendingClonedAudio() {
    final path = _pendingClonedAudioPath;
    _pendingClonedAudioPath = null;
    if (path != null) _deleteFile(path);
  }

  void _deleteFile(String path) {
    unawaited(_tryDeleteFile(path));
  }

  Future<void> _tryDeleteFile(String path) async {
    try {
      await File(path).delete();
    } on FileSystemException {
      // Temporary audio is also removed the next time the service starts.
    }
  }

  String _friendlyError(Object error) {
    if (error is XtssServiceException) return error.message;
    final message = error.toString().replaceFirst('Bad state: ', '').trim();
    return message.isEmpty
        ? 'Não foi possível reproduzir a mensagem.'
        : message;
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  Future<void> shutdown() {
    return _shutdownFuture ??= _performShutdown();
  }

  Future<void> _performShutdown() async {
    _stopRuntimePolling();
    _synthesisTicket++;
    try {
      await _tts.stop();
    } catch (_) {}
    await _xtts.close();
    _cleanupPendingClonedAudio();
  }

  @override
  void dispose() {
    _disposed = true;
    unawaited(shutdown());
    super.dispose();
  }
}
