import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:flutter/foundation.dart';

enum XtssComputeMode {
  automatic('auto', 'Automático'),
  gpu('gpu', 'GPU'),
  cpu('cpu', 'CPU');

  const XtssComputeMode(this.wireName, this.label);

  final String wireName;
  final String label;

  static XtssComputeMode fromWireName(String? value) {
    return values.firstWhere(
      (mode) => mode.wireName == value,
      orElse: () => automatic,
    );
  }
}

@immutable
class ClonedVoiceProfile {
  const ClonedVoiceProfile({
    required this.id,
    required this.name,
    required this.referenceCount,
    required this.durationSeconds,
    this.sourceCount = 1,
    this.wasTruncated = false,
    this.createdAt,
  });

  factory ClonedVoiceProfile.fromJson(Map<String, dynamic> json) {
    return ClonedVoiceProfile(
      id: json['id']?.toString() ?? '',
      name: json['name']?.toString() ?? 'Voz clonada',
      referenceCount: (json['reference_count'] as num?)?.toInt() ?? 1,
      durationSeconds: (json['duration_seconds'] as num?)?.toDouble() ?? 0,
      sourceCount: (json['source_count'] as num?)?.toInt() ?? 1,
      wasTruncated: json['truncated'] == true,
      createdAt: DateTime.tryParse(json['created_at']?.toString() ?? ''),
    );
  }

  final String id;
  final String name;
  final int referenceCount;
  final double durationSeconds;
  final int sourceCount;
  final bool wasTruncated;
  final DateTime? createdAt;
}

@immutable
class XtssRuntimeInfo {
  const XtssRuntimeInfo({
    required this.hardwareChecked,
    required this.cudaAvailable,
    required this.modelLoaded,
    required this.state,
    required this.message,
    this.gpuName,
    this.activeDevice,
    this.warning,
  });

  factory XtssRuntimeInfo.fromJson(Map<String, dynamic> json) {
    return XtssRuntimeInfo(
      hardwareChecked: json['hardware_checked'] == true,
      cudaAvailable: json['cuda_available'] == true,
      modelLoaded: json['model_loaded'] == true,
      state: json['state']?.toString() ?? 'ready',
      message: json['message']?.toString() ?? 'Serviço pronto',
      gpuName: json['gpu_name']?.toString(),
      activeDevice: json['active_device']?.toString(),
      warning: json['warning']?.toString(),
    );
  }

  final bool hardwareChecked;
  final bool cudaAvailable;
  final bool modelLoaded;
  final String state;
  final String message;
  final String? gpuName;
  final String? activeDevice;
  final String? warning;
}

class XtssServiceException implements Exception {
  const XtssServiceException(this.message);

  final String message;

  @override
  String toString() => message;
}

class XtssServiceClient {
  Process? _process;
  int? _port;
  String? _token;
  bool _ready = false;
  bool _closing = false;

  bool get isReady => _ready;
  String? availabilityError;

  Future<void> start() async {
    if (_ready) return;
    _closing = false;
    availabilityError = null;

    final executable = _findServiceExecutable();
    if (executable == null) {
      availabilityError =
          'O mecanismo XTTS não está incluído nesta compilação.';
      throw XtssServiceException(availabilityError!);
    }

    final port = await _reserveLoopbackPort();
    final token = _createToken();
    final dataDirectory = await _dataDirectory();

    final process = await Process.start(
      executable.path,
      ['--port', '$port', '--token', token, '--data-dir', dataDirectory.path],
      workingDirectory: executable.parent.path,
      runInShell: false,
      mode: ProcessStartMode.normal,
    );

    _process = process;
    _port = port;
    _token = token;

    process.stdout
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen((line) => debugPrint('SVoice XTTS: $line'));
    process.stderr
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen((line) => debugPrint('SVoice XTTS error: $line'));
    unawaited(
      process.exitCode.then((_) {
        if (!_closing && identical(_process, process)) {
          _ready = false;
        }
      }),
    );

    Object? lastError;
    final startupWatch = Stopwatch()..start();
    while (startupWatch.elapsed < const Duration(seconds: 90)) {
      try {
        await _request(
          'GET',
          '/health',
          allowStarting: true,
          timeout: const Duration(seconds: 30),
        );
        _ready = true;
        return;
      } catch (error) {
        lastError = error;
        if (await _hasExited(process)) break;
        await Future<void>.delayed(const Duration(milliseconds: 500));
      }
    }

    process.kill();
    _process = null;
    _port = null;
    _token = null;
    availabilityError =
        'O mecanismo de clonagem não conseguiu iniciar: ${lastError ?? 'erro desconhecido'}';
    throw XtssServiceException(availabilityError!);
  }

  Future<XtssRuntimeInfo> getRuntimeInfo() async {
    final json = await _request('GET', '/health');
    return XtssRuntimeInfo.fromJson(json);
  }

  Future<List<ClonedVoiceProfile>> listProfiles() async {
    final json = await _request('GET', '/profiles');
    final rawProfiles = json['profiles'];
    if (rawProfiles is! List) return const [];
    return rawProfiles
        .whereType<Map>()
        .map(
          (profile) =>
              ClonedVoiceProfile.fromJson(Map<String, dynamic>.from(profile)),
        )
        .where((profile) => profile.id.isNotEmpty)
        .toList(growable: false);
  }

  Future<ClonedVoiceProfile> addProfile({
    required String name,
    required List<String> referencePaths,
  }) async {
    final json = await _request(
      'POST',
      '/profiles',
      body: {'name': name, 'reference_paths': referencePaths},
      timeout: const Duration(minutes: 45),
    );
    return ClonedVoiceProfile.fromJson(
      Map<String, dynamic>.from(json['profile'] as Map),
    );
  }

  Future<void> deleteProfile(String id) async {
    await _request('DELETE', '/profiles/${Uri.encodeComponent(id)}');
  }

  Future<void> setComputeMode(XtssComputeMode mode) async {
    await _request('POST', '/config', body: {'compute_mode': mode.wireName});
  }

  Future<String> synthesize({
    required String text,
    required String profileId,
    required double speed,
    required XtssComputeMode computeMode,
  }) async {
    final json = await _request(
      'POST',
      '/synthesize',
      body: {
        'text': text,
        'profile_id': profileId,
        'speed': speed,
        'compute_mode': computeMode.wireName,
      },
      timeout: const Duration(minutes: 45),
    );
    final outputPath = json['output_path']?.toString();
    if (outputPath == null || outputPath.isEmpty) {
      throw const XtssServiceException(
        'O mecanismo XTTS não retornou o áudio gerado.',
      );
    }
    return outputPath;
  }

  Future<void> cancelAndRestart() async {
    final process = _process;
    _ready = false;
    _closing = true;
    if (process != null) {
      process.kill();
      try {
        await process.exitCode.timeout(const Duration(seconds: 4));
      } catch (_) {}
    }
    _process = null;
    _port = null;
    _token = null;
    _closing = false;
    await start();
  }

  Future<void> close() async {
    _closing = true;
    if (_ready) {
      try {
        await _request(
          'POST',
          '/shutdown',
          timeout: const Duration(seconds: 2),
        );
      } catch (_) {}
    }
    final process = _process;
    if (process != null) {
      try {
        await process.exitCode.timeout(const Duration(seconds: 3));
      } catch (_) {
        process.kill();
      }
    }
    _ready = false;
    _process = null;
    _port = null;
    _token = null;
  }

  Future<Map<String, dynamic>> _request(
    String method,
    String path, {
    Map<String, dynamic>? body,
    Duration timeout = const Duration(seconds: 15),
    bool allowStarting = false,
  }) async {
    final port = _port;
    final token = _token;
    if (port == null || token == null || (!allowStarting && !_ready)) {
      throw XtssServiceException(
        availabilityError ?? 'O mecanismo XTTS não está disponível.',
      );
    }

    final client = HttpClient()..connectionTimeout = const Duration(seconds: 3);
    try {
      final request = await client.openUrl(
        method,
        Uri.parse('http://127.0.0.1:$port$path'),
      );
      request.headers.set(HttpHeaders.authorizationHeader, 'Bearer $token');
      request.headers.contentType = ContentType.json;
      if (body != null) request.write(jsonEncode(body));
      final response = await request.close().timeout(timeout);
      final responseText = await utf8.decoder.bind(response).join();
      Map<String, dynamic> json = const {};
      if (responseText.isNotEmpty) {
        final decoded = jsonDecode(responseText);
        if (decoded is Map) json = Map<String, dynamic>.from(decoded);
      }
      if (response.statusCode < 200 || response.statusCode >= 300) {
        throw XtssServiceException(
          json['error']?.toString() ??
              'Falha no mecanismo XTTS (${response.statusCode}).',
        );
      }
      return json;
    } on TimeoutException {
      throw const XtssServiceException(
        'O mecanismo XTTS demorou mais do que o esperado.',
      );
    } on SocketException catch (error) {
      throw XtssServiceException(
        'Não foi possível comunicar com o mecanismo XTTS: ${error.message}',
      );
    } finally {
      client.close(force: true);
    }
  }

  File? _findServiceExecutable() {
    final override = Platform.environment['SVOICE_XTTS_SERVICE_PATH'];
    final executableDirectory = File(Platform.resolvedExecutable).parent;
    final currentDirectory = Directory.current;
    final candidates = <String>[
      if (override != null && override.trim().isNotEmpty) override.trim(),
      '${executableDirectory.path}${Platform.pathSeparator}xtts_service${Platform.pathSeparator}svoice_xtts_service.exe',
      '${currentDirectory.path}${Platform.pathSeparator}python_service${Platform.pathSeparator}dist${Platform.pathSeparator}svoice_xtts_service${Platform.pathSeparator}svoice_xtts_service.exe',
    ];
    for (final path in candidates) {
      final file = File(path);
      if (file.existsSync()) return file;
    }
    return null;
  }

  Future<Directory> _dataDirectory() async {
    final localAppData = Platform.environment['LOCALAPPDATA'];
    final base = localAppData == null || localAppData.isEmpty
        ? Directory.systemTemp.path
        : localAppData;
    final directory = Directory(
      '$base${Platform.pathSeparator}SVoice${Platform.pathSeparator}XTTS',
    );
    await directory.create(recursive: true);
    return directory;
  }

  Future<int> _reserveLoopbackPort() async {
    final socket = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final port = socket.port;
    await socket.close();
    return port;
  }

  String _createToken() {
    final random = Random.secure();
    return List<int>.generate(
      32,
      (_) => random.nextInt(256),
    ).map((value) => value.toRadixString(16).padLeft(2, '0')).join();
  }

  Future<bool> _hasExited(Process process) async {
    try {
      await process.exitCode.timeout(const Duration(milliseconds: 1));
      return true;
    } on TimeoutException {
      return false;
    }
  }
}
