import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:file_selector/file_selector.dart';
import 'package:hotkey_manager/hotkey_manager.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:window_manager/window_manager.dart';

import 'app_controller.dart';
import 'overlay_window_controller.dart';
import 'xtts_service_client.dart';

enum PanelMode { compact, chat, settings, discordGuide }

class OverlayScreen extends StatefulWidget {
  const OverlayScreen({
    super.key,
    required this.controller,
    this.enableDesktopFeatures = true,
    this.manageControllerLifecycle = true,
  });

  final SVoiceController controller;
  final bool enableDesktopFeatures;
  final bool manageControllerLifecycle;

  @override
  State<OverlayScreen> createState() => _OverlayScreenState();
}

class _OverlayScreenState extends State<OverlayScreen> with WindowListener {
  static const accent = Color(0xFF8BE9D2);
  static const lavender = Color(0xFFAAB8FF);
  static const _shortcutPreferenceKey = 'visibilityHotKey';

  final _messageController = TextEditingController();
  final _messageFocus = FocusNode();
  final _windowController = OverlayWindowController(
    window: const WindowManagerOverlayWindow(),
  );
  HotKey? _visibilityHotKey;
  String? _visibilityHotKeyError;
  PanelMode _mode = PanelMode.chat;
  bool _alwaysOnTop = true;
  bool _isClosing = false;

  SVoiceController get controller => widget.controller;

  @override
  void initState() {
    super.initState();
    controller.addListener(_handleControllerChange);
    if (widget.enableDesktopFeatures) {
      windowManager.addListener(this);
      windowManager.setPreventClose(true);
      _loadAndRegisterVisibilityHotKey();
    }
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _messageFocus.requestFocus();
    });
  }

  HotKey _defaultVisibilityHotKey() => HotKey(
    key: PhysicalKeyboardKey.quote,
    modifiers: const [HotKeyModifier.shift],
    scope: HotKeyScope.system,
  );

  Future<void> _loadAndRegisterVisibilityHotKey() async {
    final preferences = await SharedPreferences.getInstance();
    final storedValue = preferences.getString(_shortcutPreferenceKey);
    var hotKey = _defaultVisibilityHotKey();
    if (storedValue != null) {
      try {
        hotKey = HotKey.fromJson(
          Map<String, dynamic>.from(jsonDecode(storedValue) as Map),
        );
      } catch (_) {
        await preferences.remove(_shortcutPreferenceKey);
      }
    }
    if (!mounted) return;
    try {
      await _registerVisibilityHotKey(hotKey);
    } catch (error, stackTrace) {
      debugPrint('Não foi possível registrar a hotkey global: $error');
      debugPrintStack(stackTrace: stackTrace);
      if (mounted) {
        setState(() {
          _visibilityHotKeyError =
              'Atalho indisponível — clique para escolher outro';
        });
      }
    }
  }

  Future<void> _registerVisibilityHotKey(HotKey hotKey) async {
    final previous = _visibilityHotKey;
    if (previous != null) {
      await hotKeyManager.unregister(previous);
      _visibilityHotKey = null;
    }
    await hotKeyManager.register(
      hotKey,
      keyDownHandler: (_) => _toggleVisibility(),
    );
    _visibilityHotKey = hotKey;
    _visibilityHotKeyError = null;
    if (mounted) setState(() {});
  }

  Future<void> _toggleVisibility() async {
    try {
      final change = await _windowController.toggle();
      if (change == OverlayVisibilityChange.shown && mounted) {
        _messageFocus.requestFocus();
      }
    } catch (error, stackTrace) {
      debugPrint('Não foi possível alternar a janela do SVoice: $error');
      debugPrintStack(stackTrace: stackTrace);
    }
  }

  void _handleControllerChange() {
    if (mounted) setState(() {});
  }

  @override
  void onWindowClose() {
    _closeApplication();
  }

  @override
  void onWindowMinimize() {
    _windowController.notifyMinimized();
  }

  @override
  void onWindowRestore() {
    _windowController.notifyRestored();
  }

  Future<void> _closeApplication() async {
    if (_isClosing) return;
    _isClosing = true;
    await controller.shutdown();
    await windowManager.setPreventClose(false);
    await windowManager.close();
  }

  @override
  void dispose() {
    controller.removeListener(_handleControllerChange);
    final visibilityHotKey = _visibilityHotKey;
    if (widget.enableDesktopFeatures) {
      windowManager.removeListener(this);
      if (visibilityHotKey != null) {
        hotKeyManager.unregister(visibilityHotKey);
      }
    }
    _messageController.dispose();
    _messageFocus.dispose();
    if (widget.manageControllerLifecycle) controller.dispose();
    super.dispose();
  }

  Future<void> _setMode(PanelMode mode) async {
    if (_mode == mode) return;
    setState(() => _mode = mode);
    final size = switch (mode) {
      PanelMode.compact => const Size(520, 142),
      PanelMode.chat => const Size(560, 380),
      PanelMode.settings => const Size(560, 610),
      PanelMode.discordGuide => const Size(560, 540),
    };
    if (widget.enableDesktopFeatures) {
      await windowManager.setSize(size, animate: true);
    }
    if (mode == PanelMode.compact || mode == PanelMode.chat) {
      _messageFocus.requestFocus();
    }
  }

  Future<void> _submit([String? value]) async {
    final message = (value ?? _messageController.text).trim();
    if (message.isEmpty) return;
    await controller.speak(message);
    if (controller.clearAfterSpeaking) _messageController.clear();
    _messageFocus.requestFocus();
  }

  Future<void> _toggleAlwaysOnTop() async {
    _alwaysOnTop = !_alwaysOnTop;
    await windowManager.setAlwaysOnTop(_alwaysOnTop);
    if (mounted) setState(() {});
  }

  Future<void> _configureVisibilityHotKey() async {
    final previous = _visibilityHotKey ?? _defaultVisibilityHotKey();
    if (widget.enableDesktopFeatures && _visibilityHotKey != null) {
      await hotKeyManager.unregister(_visibilityHotKey!);
      _visibilityHotKey = null;
    }
    if (!mounted) return;

    HotKey recorded = previous;
    final selected = await showDialog<HotKey>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setDialogState) {
          final valid = _isValidShortcut(recorded);
          return AlertDialog(
            backgroundColor: const Color(0xFF121922),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(18),
              side: BorderSide(color: Colors.white.withValues(alpha: .1)),
            ),
            title: const Text(
              'Atalho para mostrar ou ocultar o SVoice',
              style: TextStyle(fontSize: 15),
            ),
            content: SizedBox(
              width: 330,
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Pressione uma combinação de teclas.',
                    style: TextStyle(
                      color: Colors.white.withValues(alpha: .58),
                      fontSize: 11.5,
                    ),
                  ),
                  const SizedBox(height: 16),
                  Container(
                    width: double.infinity,
                    padding: const EdgeInsets.symmetric(
                      horizontal: 14,
                      vertical: 15,
                    ),
                    decoration: BoxDecoration(
                      color: Colors.black.withValues(alpha: .2),
                      borderRadius: BorderRadius.circular(12),
                      border: Border.all(
                        color: valid
                            ? accent.withValues(alpha: .28)
                            : Colors.white.withValues(alpha: .1),
                      ),
                    ),
                    child: Center(
                      child: HotKeyRecorder(
                        initalHotKey: previous,
                        onHotKeyRecorded: (hotKey) {
                          setDialogState(() => recorded = hotKey);
                        },
                      ),
                    ),
                  ),
                  const SizedBox(height: 10),
                  Text(
                    valid
                        ? _shortcutLabel(recorded)
                        : 'Use pelo menos um modificador e outra tecla.',
                    style: TextStyle(
                      color: valid
                          ? accent.withValues(alpha: .72)
                          : const Color(0xFFFFC776).withValues(alpha: .8),
                      fontSize: 10.5,
                    ),
                  ),
                ],
              ),
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(dialogContext),
                child: const Text('Cancelar'),
              ),
              FilledButton(
                onPressed: valid
                    ? () => Navigator.pop(dialogContext, recorded)
                    : null,
                style: FilledButton.styleFrom(
                  backgroundColor: accent,
                  foregroundColor: const Color(0xFF091016),
                ),
                child: const Text('Salvar'),
              ),
            ],
          );
        },
      ),
    );

    final chosen = selected ?? previous;
    try {
      if (widget.enableDesktopFeatures) {
        await _registerVisibilityHotKey(chosen);
      } else {
        _visibilityHotKey = chosen;
      }
      if (selected != null) {
        final preferences = await SharedPreferences.getInstance();
        await preferences.setString(
          _shortcutPreferenceKey,
          jsonEncode(chosen.toJson()),
        );
      }
    } catch (error, stackTrace) {
      debugPrint('Não foi possível salvar a hotkey global: $error');
      debugPrintStack(stackTrace: stackTrace);
      if (widget.enableDesktopFeatures) {
        try {
          await _registerVisibilityHotKey(previous);
        } catch (restoreError, restoreStackTrace) {
          debugPrint(
            'Não foi possível restaurar a hotkey anterior: $restoreError',
          );
          debugPrintStack(stackTrace: restoreStackTrace);
          if (mounted) {
            setState(() {
              _visibilityHotKeyError =
                  'Atalho indisponível — clique para escolher outro';
            });
          }
        }
      }
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text(
              'Esse atalho já está sendo usado por outro programa.',
            ),
          ),
        );
      }
    }
  }

  bool _isValidShortcut(HotKey hotKey) {
    final modifiers = hotKey.modifiers ?? const <HotKeyModifier>[];
    final isModifierKey = HotKeyModifier.values.any(
      (modifier) => modifier.physicalKeys.contains(hotKey.physicalKey),
    );
    return modifiers.isNotEmpty && !isModifierKey;
  }

  String _shortcutLabel([HotKey? hotKey]) {
    final current = hotKey ?? _visibilityHotKey ?? _defaultVisibilityHotKey();
    final parts = <String>[
      for (final modifier in current.modifiers ?? const <HotKeyModifier>[])
        switch (modifier) {
          HotKeyModifier.control => 'Ctrl',
          HotKeyModifier.alt => 'Alt',
          HotKeyModifier.shift => 'Shift',
          HotKeyModifier.meta => 'Win',
          HotKeyModifier.capsLock => 'Caps Lock',
          HotKeyModifier.fn => 'Fn',
        },
      current.physicalKey == PhysicalKeyboardKey.quote
          ? 'Aspas'
          : (current.physicalKey.keyLabel.isNotEmpty
                ? current.physicalKey.keyLabel.toUpperCase()
                : current.physicalKey.debugName ?? 'Tecla'),
    ];
    return parts.join(' + ');
  }

  Future<void> _openVolumeMixer() async {
    try {
      await Process.start('explorer.exe', ['ms-settings:apps-volume']);
    } catch (_) {
      await Process.start('explorer.exe', ['ms-settings:sound']);
    }
  }

  Future<void> _openVbAudioPage() async {
    await Process.start('explorer.exe', ['https://vb-audio.com/Cable/']);
  }

  KeyEventResult _handleKeyEvent(FocusNode node, KeyEvent event) {
    if (event is KeyDownEvent &&
        event.logicalKey == LogicalKeyboardKey.escape) {
      controller.stop();
      return KeyEventResult.handled;
    }
    return KeyEventResult.ignored;
  }

  @override
  Widget build(BuildContext context) {
    final alpha = controller.panelOpacity.clamp(.42, .96);
    final panelColor = const Color(0xFF0C1119).withValues(alpha: alpha);

    return Focus(
      onKeyEvent: _handleKeyEvent,
      child: Scaffold(
        backgroundColor: Colors.transparent,
        body: Padding(
          padding: const EdgeInsets.all(10),
          child: DecoratedBox(
            decoration: BoxDecoration(
              color: panelColor,
              borderRadius: BorderRadius.circular(19),
              border: Border.all(color: Colors.white.withValues(alpha: .13)),
              boxShadow: [
                BoxShadow(
                  color: Colors.black.withValues(alpha: .34),
                  blurRadius: 28,
                  spreadRadius: -8,
                  offset: const Offset(0, 12),
                ),
              ],
            ),
            child: ClipRRect(
              borderRadius: BorderRadius.circular(18),
              child: Stack(
                children: [
                  Positioned(
                    top: -90,
                    right: -70,
                    child: Container(
                      width: 230,
                      height: 190,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        gradient: RadialGradient(
                          colors: [
                            lavender.withValues(alpha: .12),
                            Colors.transparent,
                          ],
                        ),
                      ),
                    ),
                  ),
                  Column(
                    children: [
                      _buildHeader(),
                      Expanded(child: _buildCurrentPanel()),
                    ],
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildHeader() {
    final status = switch (controller.status) {
      VoiceStatus.loading => ('INICIANDO', lavender),
      VoiceStatus.ready => ('PRONTO', accent),
      VoiceStatus.generating => ('GERANDO', lavender),
      VoiceStatus.speaking => ('FALANDO', const Color(0xFFFFC776)),
      VoiceStatus.error => ('ERRO', const Color(0xFFFF7C87)),
    };

    return GestureDetector(
      behavior: HitTestBehavior.translucent,
      onPanStart: (_) => windowManager.startDragging(),
      child: SizedBox(
        height: 52,
        child: Padding(
          padding: const EdgeInsets.only(left: 15, right: 7),
          child: Row(
            children: [
              Container(
                width: 27,
                height: 27,
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(9),
                  gradient: const LinearGradient(colors: [accent, lavender]),
                ),
                child: const Icon(
                  Icons.graphic_eq_rounded,
                  size: 17,
                  color: Color(0xFF091016),
                ),
              ),
              const SizedBox(width: 10),
              const Text(
                'SVOICE',
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w700,
                  letterSpacing: 1.7,
                ),
              ),
              const SizedBox(width: 10),
              Container(
                width: 5,
                height: 5,
                decoration: BoxDecoration(
                  color: status.$2,
                  shape: BoxShape.circle,
                  boxShadow: [
                    BoxShadow(color: status.$2, blurRadius: 7, spreadRadius: 1),
                  ],
                ),
              ),
              const SizedBox(width: 6),
              Text(
                status.$1,
                style: TextStyle(
                  color: status.$2.withValues(alpha: .9),
                  fontSize: 9,
                  fontWeight: FontWeight.w700,
                  letterSpacing: 1.1,
                ),
              ),
              const Spacer(),
              if (_mode == PanelMode.chat || _mode == PanelMode.compact)
                _headerButton(
                  icon: _mode == PanelMode.compact
                      ? Icons.unfold_more_rounded
                      : Icons.unfold_less_rounded,
                  tooltip: _mode == PanelMode.compact ? 'Expandir' : 'Recolher',
                  onPressed: () => _setMode(
                    _mode == PanelMode.compact
                        ? PanelMode.chat
                        : PanelMode.compact,
                  ),
                ),
              _headerButton(
                icon: _alwaysOnTop
                    ? Icons.push_pin_rounded
                    : Icons.push_pin_outlined,
                tooltip: _alwaysOnTop
                    ? 'Desafixar da tela'
                    : 'Manter sobre outros apps',
                active: _alwaysOnTop,
                onPressed: _toggleAlwaysOnTop,
              ),
              _headerButton(
                icon: Icons.tune_rounded,
                tooltip: 'Ajustes',
                active: _mode == PanelMode.settings,
                onPressed: () => _setMode(
                  _mode == PanelMode.settings
                      ? PanelMode.chat
                      : PanelMode.settings,
                ),
              ),
              _headerButton(
                icon: Icons.remove_rounded,
                tooltip: 'Minimizar',
                onPressed: _windowController.minimize,
              ),
              _headerButton(
                icon: Icons.close_rounded,
                tooltip: 'Fechar',
                onPressed: _closeApplication,
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _headerButton({
    required IconData icon,
    required String tooltip,
    required VoidCallback onPressed,
    bool active = false,
  }) {
    return SizedBox(
      width: 31,
      height: 31,
      child: IconButton(
        padding: EdgeInsets.zero,
        tooltip: tooltip,
        onPressed: onPressed,
        icon: Icon(
          icon,
          size: 16,
          color: active ? accent : Colors.white.withValues(alpha: .55),
        ),
      ),
    );
  }

  Widget _buildCurrentPanel() {
    return switch (_mode) {
      PanelMode.compact => _buildCompactPanel(),
      PanelMode.chat => _buildChatPanel(),
      PanelMode.settings => _buildSettingsPanel(),
      PanelMode.discordGuide => _buildDiscordGuide(),
    };
  }

  Widget _buildCompactPanel() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(14, 0, 14, 14),
      child: _buildComposer(compact: true),
    );
  }

  Widget _buildChatPanel() {
    return Column(
      children: [
        Expanded(
          child: controller.history.isEmpty
              ? _buildEmptyState()
              : ListView.separated(
                  reverse: true,
                  padding: const EdgeInsets.fromLTRB(18, 8, 18, 12),
                  itemCount: controller.history.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 7),
                  itemBuilder: (context, index) {
                    final message = controller.history[index];
                    return _buildHistoryItem(message);
                  },
                ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(14, 0, 14, 8),
          child: _buildComposer(),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(18, 0, 18, 12),
          child: Row(
            children: [
              Icon(
                Icons.keyboard_return_rounded,
                size: 12,
                color: Colors.white.withValues(alpha: .34),
              ),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  'Enter: falar  •  Esc: parar',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    color: Colors.white.withValues(alpha: .34),
                    fontSize: 10,
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Text(
                _shortcutLabel(),
                style: TextStyle(
                  color: Colors.white.withValues(alpha: .27),
                  fontSize: 10,
                  letterSpacing: .25,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildEmptyState() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            Icons.chat_bubble_outline_rounded,
            size: 24,
            color: Colors.white.withValues(alpha: .24),
          ),
          const SizedBox(height: 9),
          Text(
            'Digite uma mensagem para dar voz ao texto',
            style: TextStyle(
              color: Colors.white.withValues(alpha: .48),
              fontSize: 12,
            ),
          ),
          const SizedBox(height: 5),
          TextButton.icon(
            onPressed: () => _setMode(PanelMode.discordGuide),
            icon: const Icon(Icons.headset_mic_rounded, size: 14),
            label: const Text('Conectar ao Discord'),
            style: TextButton.styleFrom(
              foregroundColor: accent.withValues(alpha: .78),
              textStyle: const TextStyle(fontSize: 11),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildHistoryItem(SpokenMessage message) {
    return Align(
      alignment: Alignment.centerRight,
      child: InkWell(
        borderRadius: BorderRadius.circular(13),
        onTap: () => controller.speak(message.text, addToHistory: false),
        child: Container(
          constraints: const BoxConstraints(maxWidth: 430),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
          decoration: BoxDecoration(
            color: Colors.white.withValues(alpha: .055),
            borderRadius: BorderRadius.circular(13),
            border: Border.all(color: Colors.white.withValues(alpha: .07)),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Flexible(
                child: Text(
                  message.text,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    color: Colors.white.withValues(alpha: .76),
                    fontSize: 12,
                    height: 1.35,
                  ),
                ),
              ),
              const SizedBox(width: 9),
              Icon(
                Icons.replay_rounded,
                size: 14,
                color: accent.withValues(alpha: .55),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildComposer({bool compact = false}) {
    return Container(
      height: 54,
      decoration: BoxDecoration(
        color: Colors.black.withValues(alpha: .18),
        borderRadius: BorderRadius.circular(15),
        border: Border.all(
          color: controller.isSpeaking
              ? accent.withValues(alpha: .34)
              : Colors.white.withValues(alpha: .1),
        ),
      ),
      child: Row(
        children: [
          const SizedBox(width: 15),
          Expanded(
            child: TextField(
              controller: _messageController,
              focusNode: _messageFocus,
              autofocus: true,
              enabled: controller.status != VoiceStatus.loading,
              textInputAction: TextInputAction.send,
              onSubmitted: _submit,
              maxLines: 1,
              maxLength: 500,
              buildCounter: (
                _, {
                required currentLength,
                required isFocused,
                maxLength,
              }) => null,
              style: const TextStyle(fontSize: 13.5, height: 1.2),
              cursorColor: accent,
              decoration: InputDecoration(
                border: InputBorder.none,
                isCollapsed: true,
                hintText: controller.status == VoiceStatus.error
                    ? controller.errorMessage
                    : controller.status == VoiceStatus.generating
                    ? controller.cloningStatusMessage ?? 'Gerando voz clonada…'
                    : compact
                    ? 'Digite e pressione Enter…'
                    : 'Escreva o que você quer dizer…',
                hintStyle: TextStyle(
                  color: controller.status == VoiceStatus.error
                      ? const Color(0xFFFF7C87).withValues(alpha: .72)
                      : Colors.white.withValues(alpha: .31),
                  fontSize: 13,
                ),
              ),
            ),
          ),
          if (controller.isBusy)
            _composerButton(
              icon: Icons.stop_rounded,
              color: const Color(0xFFFFC776),
              tooltip: 'Parar',
              onPressed: controller.stop,
            )
          else
            _composerButton(
              icon: Icons.arrow_upward_rounded,
              color: accent,
              tooltip: 'Falar',
              onPressed: _submit,
            ),
          const SizedBox(width: 7),
        ],
      ),
    );
  }

  Widget _composerButton({
    required IconData icon,
    required Color color,
    required String tooltip,
    required VoidCallback onPressed,
  }) {
    return Tooltip(
      message: tooltip,
      child: InkWell(
        borderRadius: BorderRadius.circular(12),
        onTap: onPressed,
        child: Container(
          width: 39,
          height: 39,
          decoration: BoxDecoration(
            color: color.withValues(alpha: .14),
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: color.withValues(alpha: .2)),
          ),
          child: Icon(icon, size: 18, color: color),
        ),
      ),
    );
  }

  Widget _buildSettingsPanel() {
    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(18, 4, 18, 20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _sectionTitle('VOZ'),
          const SizedBox(height: 8),
          _voiceDropdown(),
          const SizedBox(height: 10),
          _clonedVoicesCard(),
          const SizedBox(height: 15),
          _sliderRow(
            label: 'Velocidade',
            value: controller.rate,
            min: 0,
            max: 1,
            displayValue: '${(controller.rate * 200).round()}%',
            onChanged: controller.updateRate,
          ),
          _sliderRow(
            label: 'Tom',
            value: controller.pitch,
            min: .5,
            max: 2,
            displayValue: '${controller.pitch.toStringAsFixed(1)}×',
            onChanged: controller.updatePitch,
            enabled: !controller.isUsingClonedVoice,
          ),
          _sliderRow(
            label: 'Volume',
            value: controller.volume,
            min: 0,
            max: 1,
            displayValue: '${(controller.volume * 100).round()}%',
            onChanged: controller.updateVolume,
          ),
          const SizedBox(height: 8),
          _sectionTitle('SAÍDA DE ÁUDIO'),
          const SizedBox(height: 8),
          _audioDeviceDropdown(),
          const SizedBox(height: 4),
          _settingSwitch(
            title: 'Modo Eco',
            subtitle: controller.isUsingVirtualCable
                ? 'Ouvir também na saída padrão do Windows.'
                : 'Selecione o CABLE Input para monitorar a voz.',
            value: controller.isEchoActive,
            onChanged: controller.updateEchoEnabled,
            enabled: controller.isUsingVirtualCable,
          ),
          const SizedBox(height: 7),
          Row(
            children: [
              Icon(
                controller.hasVirtualCable
                    ? Icons.check_circle_rounded
                    : Icons.info_outline_rounded,
                size: 13,
                color: controller.hasVirtualCable
                    ? accent
                    : const Color(0xFFFFC776),
              ),
              const SizedBox(width: 7),
              Expanded(
                child: Text(
                  controller.hasVirtualCable
                      ? controller.isUsingVirtualCable
                            ? 'VB-CABLE detectado e selecionado automaticamente.'
                            : 'VB-CABLE detectado; selecione-o para usar no Discord.'
                      : 'O instalador completo adiciona o microfone virtual.',
                  style: TextStyle(
                    color: Colors.white.withValues(alpha: .4),
                    fontSize: 10,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 13),
          _sectionTitle('OVERLAY'),
          const SizedBox(height: 5),
          _sliderRow(
            label: 'Transparência',
            value: controller.panelOpacity,
            min: .42,
            max: .96,
            displayValue: '${(controller.panelOpacity * 100).round()}%',
            onChanged: controller.updatePanelOpacity,
          ),
          _settingSwitch(
            title: 'Limpar texto depois de falar',
            value: controller.clearAfterSpeaking,
            onChanged: controller.updateClearAfterSpeaking,
          ),
          const SizedBox(height: 10),
          _sectionTitle('ATALHO GLOBAL'),
          const SizedBox(height: 8),
          _shortcutSettingCard(),
          const SizedBox(height: 14),
          _discordCard(),
          const SizedBox(height: 12),
          Row(
            children: [
              Expanded(
                child: Text(
                  'Ocultar e mostrar de qualquer lugar',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    color: Colors.white.withValues(alpha: .34),
                    fontSize: 10.5,
                  ),
                ),
              ),
              const SizedBox(width: 8),
              TextButton(
                onPressed: () async {
                  await controller.saveSettings();
                  await _setMode(PanelMode.chat);
                },
                child: const Text('Concluir'),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _sectionTitle(String title) {
    return Text(
      title,
      style: TextStyle(
        color: Colors.white.withValues(alpha: .36),
        fontSize: 9.5,
        fontWeight: FontWeight.w700,
        letterSpacing: 1.35,
      ),
    );
  }

  Widget _shortcutSettingCard() {
    return InkWell(
      borderRadius: BorderRadius.circular(13),
      onTap: _configureVisibilityHotKey,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 11),
        decoration: BoxDecoration(
          color: Colors.white.withValues(alpha: .045),
          borderRadius: BorderRadius.circular(13),
          border: Border.all(color: Colors.white.withValues(alpha: .08)),
        ),
        child: Row(
          children: [
            Container(
              width: 32,
              height: 32,
              decoration: BoxDecoration(
                color: accent.withValues(alpha: .1),
                borderRadius: BorderRadius.circular(9),
              ),
              child: const Icon(
                Icons.visibility_off_outlined,
                size: 17,
                color: accent,
              ),
            ),
            const SizedBox(width: 11),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text(
                    'Ocultar ou mostrar o SVoice',
                    style: TextStyle(fontSize: 11.5),
                  ),
                  const SizedBox(height: 3),
                  Text(
                    _visibilityHotKeyError ?? _shortcutLabel(),
                    style: TextStyle(
                      color: _visibilityHotKeyError == null
                          ? accent.withValues(alpha: .72)
                          : const Color(0xFFFF7C87),
                      fontSize: 10.5,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ],
              ),
            ),
            Text(
              'Alterar',
              style: TextStyle(
                color: Colors.white.withValues(alpha: .42),
                fontSize: 10.5,
              ),
            ),
            const SizedBox(width: 4),
            Icon(
              Icons.chevron_right_rounded,
              size: 17,
              color: Colors.white.withValues(alpha: .3),
            ),
          ],
        ),
      ),
    );
  }

  Widget _voiceDropdown() {
    return DropdownButtonFormField<VoiceOption>(
      key: ValueKey(
        '${controller.selectedVoice?.id}:${controller.voices.length}',
      ),
      initialValue: controller.selectedVoice,
      isExpanded: true,
      dropdownColor: const Color(0xFF171E29),
      iconEnabledColor: Colors.white54,
      style: TextStyle(color: Colors.white.withValues(alpha: .8), fontSize: 12),
      decoration: InputDecoration(
        filled: true,
        fillColor: Colors.white.withValues(alpha: .045),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 12,
          vertical: 10,
        ),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.white.withValues(alpha: .08)),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.white.withValues(alpha: .08)),
        ),
      ),
      hint: const Text('Nenhuma voz encontrada'),
      items: controller.voices
          .map(
            (voice) => DropdownMenuItem(
              value: voice,
              child: Row(
                children: [
                  Icon(
                    voice.isCloned
                        ? Icons.record_voice_over_rounded
                        : Icons.window_rounded,
                    size: 14,
                    color: voice.isCloned ? lavender : Colors.white38,
                  ),
                  const SizedBox(width: 7),
                  Expanded(
                    child: Text(voice.label, overflow: TextOverflow.ellipsis),
                  ),
                ],
              ),
            ),
          )
          .toList(),
      onChanged: controller.selectVoice,
    );
  }

  Widget _clonedVoicesCard() {
    final available = controller.cloningAvailable;
    final profiles = controller.clonedVoiceProfiles;
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: lavender.withValues(alpha: .045),
        borderRadius: BorderRadius.circular(13),
        border: Border.all(color: lavender.withValues(alpha: .12)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.graphic_eq_rounded, size: 17, color: lavender),
              const SizedBox(width: 8),
              const Expanded(
                child: Text(
                  'Vozes clonadas',
                  style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600),
                ),
              ),
              TextButton.icon(
                onPressed: available ? _addClonedVoice : null,
                icon: const Icon(Icons.add_rounded, size: 15),
                label: const Text('Adicionar'),
                style: TextButton.styleFrom(
                  visualDensity: VisualDensity.compact,
                  textStyle: const TextStyle(fontSize: 10.5),
                ),
              ),
            ],
          ),
          const SizedBox(height: 4),
          Text(
            controller.cloningSummary,
            style: TextStyle(
              color: available
                  ? Colors.white.withValues(alpha: .42)
                  : const Color(0xFFFFC776).withValues(alpha: .8),
              fontSize: 10,
            ),
          ),
          if (available) ...[
            const SizedBox(height: 9),
            Row(
              children: [
                Text(
                  'Processar com',
                  style: TextStyle(
                    color: Colors.white.withValues(alpha: .5),
                    fontSize: 10.5,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: DropdownButtonHideUnderline(
                    child: DropdownButton<XtssComputeMode>(
                      value: controller.cloningComputeMode,
                      isDense: true,
                      dropdownColor: const Color(0xFF171E29),
                      style: const TextStyle(
                        color: Colors.white70,
                        fontSize: 11,
                      ),
                      items: XtssComputeMode.values
                          .map(
                            (mode) => DropdownMenuItem(
                              value: mode,
                              child: Text(
                                mode == XtssComputeMode.automatic
                                    ? 'Automático (GPU → CPU)'
                                    : mode.label,
                              ),
                            ),
                          )
                          .toList(),
                      onChanged: (mode) {
                        if (mode != null) {
                          controller.updateCloningComputeMode(mode);
                        }
                      },
                    ),
                  ),
                ),
              ],
            ),
          ],
          if (profiles.isNotEmpty) ...[
            const SizedBox(height: 8),
            for (final profile in profiles)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Row(
                  children: [
                    const Icon(Icons.mic_none_rounded, size: 14, color: accent),
                    const SizedBox(width: 7),
                    Expanded(
                      child: Text(
                        profile.name,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontSize: 10.5),
                      ),
                    ),
                    Text(
                      _formatReferenceDuration(profile.durationSeconds),
                      style: TextStyle(
                        color: Colors.white.withValues(alpha: .3),
                        fontSize: 9.5,
                      ),
                    ),
                    SizedBox(
                      width: 28,
                      height: 28,
                      child: IconButton(
                        padding: EdgeInsets.zero,
                        tooltip: 'Excluir perfil',
                        onPressed: () => _deleteClonedVoice(profile),
                        icon: Icon(
                          Icons.delete_outline_rounded,
                          size: 15,
                          color: Colors.white.withValues(alpha: .35),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
          ],
          if (controller.cloningStatusMessage != null) ...[
            const SizedBox(height: 7),
            Text(
              controller.cloningStatusMessage!,
              style: const TextStyle(color: lavender, fontSize: 10),
            ),
          ],
          const SizedBox(height: 5),
          Text(
            'Na primeira fala, o SVoice baixa o XTTSv2 (~2 GB). CPU funciona sem placa de vídeo, mas leva mais tempo.',
            style: TextStyle(
              color: Colors.white.withValues(alpha: .3),
              fontSize: 9.5,
              height: 1.35,
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _addClonedVoice() async {
    const audioTypes = XTypeGroup(
      label: 'Áudio de referência',
      extensions: ['wav', 'mp3', 'm4a', 'flac', 'ogg'],
    );
    List<XFile> audioFiles;
    try {
      audioFiles = await openFiles(acceptedTypeGroups: [audioTypes]);
    } catch (_) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text(
            'Não foi possível abrir o seletor de arquivos. Tente novamente.',
          ),
        ),
      );
      return;
    }
    if (audioFiles.isEmpty || !mounted) return;

    List<String> referencePaths;
    try {
      referencePaths = validateVoiceReferencePaths(
        audioFiles.map((audio) => audio.path),
      );
    } on XtssServiceException catch (error) {
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(error.message)));
      return;
    }

    final selectedNames = referencePaths
        .map((path) => File(path).uri.pathSegments.last)
        .toList(growable: false);
    final automaticName = selectedNames.first.replaceFirst(
      RegExp(r'\.[^.]+$'),
      '',
    );
    final nameController = TextEditingController();
    var saving = false;
    String? dialogError;

    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setDialogState) => AlertDialog(
          backgroundColor: const Color(0xFF111822),
          title: const Text(
            'Adicionar voz clonada',
            style: TextStyle(fontSize: 16),
          ),
          content: SizedBox(
            width: 430,
            child: SingleChildScrollView(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  TextField(
                    controller: nameController,
                    autofocus: true,
                    enabled: !saving,
                    maxLength: 80,
                    maxLengthEnforcement: MaxLengthEnforcement.enforced,
                    inputFormatters: [
                      FilteringTextInputFormatter.deny(
                        RegExp(r'[\x00-\x1F\x7F]'),
                      ),
                    ],
                    decoration: InputDecoration(
                      labelText: 'Nome da voz (opcional)',
                      hintText: 'Automático: $automaticName',
                      counterText: '',
                    ),
                  ),
                  const SizedBox(height: 12),
                  Text(
                    selectedNames.length == 1
                        ? selectedNames.first
                        : '${selectedNames.length} áudios selecionados',
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      color: accent.withValues(alpha: .8),
                      fontSize: 11,
                    ),
                  ),
                  const SizedBox(height: 5),
                  Text(
                    'Até 30 minutos no total. O SVoice corta os áudios em trechos menores e descarta automaticamente o que ultrapassar esse limite.',
                    style: TextStyle(
                      color: Colors.white.withValues(alpha: .4),
                      fontSize: 10.5,
                      height: 1.35,
                    ),
                  ),
                  const SizedBox(height: 8),
                  TextButton(
                    onPressed: saving ? null : _openXttsLicense,
                    child: const Text(
                      'Ler a licença oficial',
                      style: TextStyle(fontSize: 10.5),
                    ),
                  ),
                  if (dialogError != null)
                    Text(
                      dialogError!,
                      style: const TextStyle(
                        color: Color(0xFFFF7C87),
                        fontSize: 10.5,
                      ),
                    ),
                  if (saving) ...[
                    const SizedBox(height: 8),
                    const LinearProgressIndicator(minHeight: 2),
                  ],
                ],
              ),
            ),
          ),
          actions: [
            TextButton(
              onPressed: saving ? null : () => Navigator.pop(dialogContext),
              child: const Text('Cancelar'),
            ),
            FilledButton(
              onPressed: saving
                  ? null
                  : () async {
                      final name = nameController.text.trim();
                      setDialogState(() {
                        saving = true;
                        dialogError = null;
                      });
                      try {
                        final profile = await controller.addClonedVoice(
                          name: name,
                          referencePaths: referencePaths,
                        );
                        if (dialogContext.mounted) {
                          Navigator.pop(dialogContext);
                        }
                        if (mounted) {
                          ScaffoldMessenger.of(this.context).showSnackBar(
                            SnackBar(
                              content: Text(
                                profile.wasTruncated
                                    ? 'Voz “${profile.name}” adicionada. O áudio foi limitado aos primeiros 30 minutos.'
                                    : 'Voz “${profile.name}” adicionada.',
                              ),
                            ),
                          );
                        }
                      } catch (error) {
                        if (!dialogContext.mounted) return;
                        setDialogState(() {
                          saving = false;
                          dialogError = error.toString();
                        });
                      }
                    },
              child: const Text('Adicionar'),
            ),
          ],
        ),
      ),
    );
    nameController.dispose();
  }

  String _formatReferenceDuration(double seconds) {
    final roundedSeconds = seconds.round();
    if (roundedSeconds < 60) return '$roundedSeconds s';
    final minutes = roundedSeconds ~/ 60;
    final remainder = roundedSeconds % 60;
    return remainder == 0 ? '$minutes min' : '$minutes min ${remainder}s';
  }

  Future<void> _deleteClonedVoice(ClonedVoiceProfile profile) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        backgroundColor: const Color(0xFF111822),
        title: const Text('Excluir voz clonada?'),
        content: Text(
          'O perfil “${profile.name}” e a cópia local do áudio de referência serão removidos.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Cancelar'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Excluir'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await controller.deleteClonedVoice(profile.id);
    } catch (error) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Não foi possível excluir: $error')),
      );
    }
  }

  Future<void> _openXttsLicense() async {
    await Process.start('explorer.exe', [
      'https://huggingface.co/coqui/XTTS-v2/blob/main/LICENSE.txt',
    ]);
  }

  Widget _audioDeviceDropdown() {
    return DropdownButtonFormField<AudioDeviceOption>(
      initialValue: controller.selectedAudioDevice,
      isExpanded: true,
      dropdownColor: const Color(0xFF171E29),
      iconEnabledColor: Colors.white54,
      style: TextStyle(color: Colors.white.withValues(alpha: .8), fontSize: 12),
      decoration: InputDecoration(
        filled: true,
        fillColor: Colors.white.withValues(alpha: .045),
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 12,
          vertical: 10,
        ),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.white.withValues(alpha: .08)),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.white.withValues(alpha: .08)),
        ),
      ),
      hint: const Text('Nenhuma saída encontrada'),
      items: controller.audioDevices
          .map(
            (device) => DropdownMenuItem(
              value: device,
              child: Row(
                children: [
                  if (device.isVirtualCable) ...[
                    const Icon(Icons.mic_rounded, size: 14, color: accent),
                    const SizedBox(width: 7),
                  ],
                  Expanded(
                    child: Text(device.name, overflow: TextOverflow.ellipsis),
                  ),
                ],
              ),
            ),
          )
          .toList(),
      onChanged: controller.selectAudioDevice,
    );
  }

  Widget _sliderRow({
    required String label,
    required double value,
    required double min,
    required double max,
    required String displayValue,
    required ValueChanged<double> onChanged,
    bool enabled = true,
  }) {
    return SizedBox(
      height: 44,
      child: Row(
        children: [
          SizedBox(
            width: 95,
            child: Text(
              label,
              style: TextStyle(
                color: Colors.white.withValues(alpha: .62),
                fontSize: 11.5,
              ),
            ),
          ),
          Expanded(
            child: SliderTheme(
              data: SliderTheme.of(context).copyWith(
                trackHeight: 2,
                activeTrackColor: accent.withValues(alpha: .78),
                inactiveTrackColor: Colors.white.withValues(alpha: .1),
                thumbColor: accent,
                thumbShape: const RoundSliderThumbShape(enabledThumbRadius: 5),
                overlayShape: const RoundSliderOverlayShape(overlayRadius: 12),
                overlayColor: accent.withValues(alpha: .08),
              ),
              child: Slider(
                value: value.clamp(min, max),
                min: min,
                max: max,
                onChanged: enabled ? onChanged : null,
                onChangeEnd: enabled ? (_) => controller.saveSettings() : null,
              ),
            ),
          ),
          SizedBox(
            width: 42,
            child: Text(
              displayValue,
              textAlign: TextAlign.right,
              style: TextStyle(
                color: Colors.white.withValues(alpha: .4),
                fontSize: 10,
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _settingSwitch({
    required String title,
    required bool value,
    required ValueChanged<bool> onChanged,
    String? subtitle,
    bool enabled = true,
  }) {
    return SizedBox(
      height: subtitle == null ? 42 : 52,
      child: Row(
        children: [
          Expanded(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: TextStyle(
                    color: Colors.white.withValues(alpha: enabled ? .62 : .3),
                    fontSize: 11.5,
                  ),
                ),
                if (subtitle != null) ...[
                  const SizedBox(height: 2),
                  Text(
                    subtitle,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      color: Colors.white.withValues(alpha: enabled ? .32 : .2),
                      fontSize: 9.5,
                    ),
                  ),
                ],
              ],
            ),
          ),
          Transform.scale(
            scale: .75,
            child: Switch(
              value: value,
              activeTrackColor: accent.withValues(alpha: .5),
              activeThumbColor: accent,
              onChanged: enabled ? onChanged : null,
            ),
          ),
        ],
      ),
    );
  }

  Widget _discordCard() {
    return InkWell(
      borderRadius: BorderRadius.circular(14),
      onTap: () => _setMode(PanelMode.discordGuide),
      child: Container(
        padding: const EdgeInsets.all(13),
        decoration: BoxDecoration(
          color: const Color(0xFF5865F2).withValues(alpha: .09),
          borderRadius: BorderRadius.circular(14),
          border: Border.all(
            color: const Color(0xFF7C87FF).withValues(alpha: .18),
          ),
        ),
        child: Row(
          children: [
            Container(
              width: 34,
              height: 34,
              decoration: BoxDecoration(
                color: const Color(0xFF5865F2).withValues(alpha: .16),
                borderRadius: BorderRadius.circular(10),
              ),
              child: const Icon(
                Icons.headset_mic_rounded,
                color: Color(0xFFAEB5FF),
                size: 18,
              ),
            ),
            const SizedBox(width: 11),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text(
                    'Conectar ao Discord',
                    style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
                  ),
                  const SizedBox(height: 3),
                  Text(
                    'Use um microfone virtual para transmitir o TTS.',
                    style: TextStyle(
                      color: Colors.white.withValues(alpha: .4),
                      fontSize: 10.5,
                    ),
                  ),
                ],
              ),
            ),
            Icon(
              Icons.chevron_right_rounded,
              color: Colors.white.withValues(alpha: .33),
              size: 20,
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildDiscordGuide() {
    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(20, 4, 20, 18),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                width: 39,
                height: 39,
                decoration: BoxDecoration(
                  color: const Color(0xFF5865F2).withValues(alpha: .14),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: const Icon(
                  Icons.headset_mic_rounded,
                  color: Color(0xFFAEB5FF),
                  size: 20,
                ),
              ),
              const SizedBox(width: 12),
              const Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Levar a voz para o Discord',
                      style: TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                    SizedBox(height: 3),
                    Text(
                      'Configuração única no Windows',
                      style: TextStyle(color: Colors.white38, fontSize: 11),
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 18),
          _guideStep(
            1,
            'Microfone virtual incluso',
            'O SVoice Setup instala o VB-CABLE automaticamente. Ele é apenas software: nenhum cabo físico é necessário.',
          ),
          _guideStep(
            2,
            'Roteamento automático',
            'Ao detectar o CABLE Input, o SVoice passa a enviar o TTS diretamente para ele.',
          ),
          _guideStep(
            3,
            'Escolha “CABLE Output” no Discord',
            'Em Voz e vídeo, selecione CABLE Output como dispositivo de entrada e teste o microfone.',
          ),
          _guideStep(
            4,
            'Reinicie depois da instalação',
            'O Windows precisa reiniciar uma vez para concluir a instalação do driver de áudio.',
            last: true,
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _openVolumeMixer,
                  icon: const Icon(Icons.volume_up_rounded, size: 16),
                  label: const Text('Abrir Mixer do Windows'),
                  style: OutlinedButton.styleFrom(
                    foregroundColor: accent,
                    side: BorderSide(color: accent.withValues(alpha: .25)),
                    padding: const EdgeInsets.symmetric(vertical: 13),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(12),
                    ),
                    textStyle: const TextStyle(fontSize: 11.5),
                  ),
                ),
              ),
              const SizedBox(width: 10),
              TextButton(
                onPressed: () => _setMode(PanelMode.chat),
                child: const Text('Voltar'),
              ),
            ],
          ),
          const SizedBox(height: 10),
          Text(
            'VB-CABLE é um donationware da VB-Audio Software. Todos os direitos pertencem à VB-Audio; contribuições ao projeto são bem-vindas.',
            style: TextStyle(
              color: Colors.white.withValues(alpha: .3),
              fontSize: 9.5,
              height: 1.4,
            ),
          ),
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton(
              onPressed: _openVbAudioPage,
              style: TextButton.styleFrom(
                padding: const EdgeInsets.only(top: 5),
                foregroundColor: lavender.withValues(alpha: .72),
                textStyle: const TextStyle(fontSize: 10),
              ),
              child: const Text('Conhecer e apoiar o VB-CABLE'),
            ),
          ),
        ],
      ),
    );
  }

  Widget _guideStep(
    int number,
    String title,
    String description, {
    bool last = false,
  }) {
    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 28,
            child: Column(
              children: [
                Container(
                  width: 24,
                  height: 24,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: accent.withValues(alpha: .11),
                    shape: BoxShape.circle,
                    border: Border.all(color: accent.withValues(alpha: .22)),
                  ),
                  child: Text(
                    '$number',
                    style: const TextStyle(
                      color: accent,
                      fontSize: 10,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                if (!last)
                  Expanded(
                    child: Container(
                      width: 1,
                      color: Colors.white.withValues(alpha: .08),
                    ),
                  ),
              ],
            ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Padding(
              padding: EdgeInsets.only(bottom: last ? 0 : 14),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    title,
                    style: const TextStyle(
                      fontSize: 11.5,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    description,
                    style: TextStyle(
                      color: Colors.white.withValues(alpha: .4),
                      fontSize: 10.5,
                      height: 1.35,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}
