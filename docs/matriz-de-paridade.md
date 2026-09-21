# Matriz de paridade — Flutter → SVoice Game Bar standalone

Inventário das funções existentes em 2026-09-21 (tag `svoice-pre-standalone`)
e destino de cada uma na solução standalone. Este documento é atualizado ao
longo da migração; a coluna **Standalone** registra o estado real verificado.

Legenda de origem: **F** = aplicativo Flutter (`lib/`), **W** = widget Game Bar
(`gamebar/SVoice.GameBar`), **B** = bridge (`gamebar/SVoice.GameBarBridge`),
**S** = serviço XTTS (`python_service/service.py`).

| # | Função | Onde existe hoje | Comportamento atual | Destino standalone | Standalone |
| --- | --- | --- | --- | --- | --- |
| 1 | Criar perfil de voz clonada | F (diálogo), W (diálogo), S (`POST /profiles`) | Seleciona 1+ áudios, nome opcional (padrão: nome do 1.º arquivo), corta em trechos de 20 s, máximo 30 min. | Widget + serviço. Condicionamento calculado na criação com progresso. | ✅ implementado — Widget (diálogo com nome e progresso; `create_profile` → `POST /profiles`) |
| 2 | Importar amostra | F (`file_selector`), W (`FileOpenPicker`) | Múltiplos arquivos; caminho local obrigatório. | Widget (`FileOpenPicker`, múltiplos). | ✅ implementado — `FileOpenPicker` multi-arquivo; validação de caminho no bridge e no serviço |
| 3 | Formatos aceitos | F, W, S | `.wav .mp3 .m4a .flac .ogg` (S converte via ffmpeg). | Iguais. Serviço valida extensão **e** conteúdo (ffprobe/wave). | ✅ implementado — Extensões iguais; conteúdo verificado por `ffprobe` (`_probe_audio`) |
| 4 | Validação/tratamento da amostra | S | Mono 24 kHz PCM16; ≥ 3 s no total; duplicados ignorados; excedente descartado. | Igual + limite de tamanho por arquivo e mensagem quando o áudio for inválido. | ✅ implementado — + limite 512 MB/arquivo, 12 arquivos, ffprobe |
| 5 | Listar perfis | F, W, S (`GET /profiles`) | Lista com nome, duração e contagem. | Widget lista e atualiza; serviço informa perfis com referências ausentes. | ✅ implementado — `GET /profiles` com `status`/`missing_references`; widget marca `(!)` |
| 6 | Selecionar perfil | F, W | Persistido (`voiceId` / `selectedVoiceProfileId`). | Widget, persistido em `LocalSettings`. | ✅ implementado — `LocalSettings[selectedVoiceProfileId]` |
| 7 | Renomear perfil | — | Não existe. | Novo: `PATCH /profiles/{id}` + widget. | ✅ implementado — `PATCH /profiles/{id}`; painel Vozes do widget |
| 8 | Excluir perfil | F, S (`DELETE /profiles/{id}`) | Confirmação e remoção da pasta do perfil. | Widget (confirmação) + serviço. | ✅ implementado — `DELETE /profiles/{id}`; painel Vozes do widget |
| 9 | Síntese XTTS v2 | F, W, B, S (`POST /synthesize`) | Texto ≤ 1000 chars, `pt`, velocidade 0,5–2,0, normalização de pico −3 dBFS. | Igual, por sentença (permite cancelar entre sentenças). | ✅ implementado — `POST /synthesize` por sentença; `MAX_TEXT_LENGTH` 1000 |
| 10 | Cancelar síntese | F (mata e reinicia o serviço), W (só para a reprodução) | Flutter: `cancelAndRestart`; widget: `Esc` para a reprodução. | Serviço: `POST /jobs/cancel` (flag verificada entre sentenças) + widget interrompe reprodução. | ✅ implementado — `POST /jobs/cancel` (cancela entre sentenças / etapas); `Esc` no widget |
| 11 | Histórico de frases | F (6 itens, clique repete), W (1 item) | Flutter mantém 6; widget mostra só a última. | Widget: até 8 itens, clique repete; persistido em `LocalSettings`. | ✅ implementado — Painel Histórico (8 itens, clique repete, persistido) |
| 12 | Reprodução no VB-CABLE | F (`flutter_tts` patch), W (`MediaPlayer.AudioDevice`) | Seleciona `CABLE Input` automaticamente. | Widget (`MediaPlayer.AudioDevice = CABLE Input`). | ✅ implementado — `ConfigureAudioOutputAsync` |
| 13 | Modo Eco | F, W | Reproduz também na saída padrão; só quando o VB-CABLE estiver ativo. | Widget (segundo `MediaPlayer`), persistido. | ✅ implementado — `_echoPlayer` + `LocalSettings[echoEnabled]` |
| 14 | Seleção de dispositivo de saída | F (lista de dispositivos) | Lista e permite escolher qualquer saída. | Widget: mostra dispositivo virtual detectado; permite escolher saída manual no painel de ajustes. | ✅ implementado — Painel Ajustes → lista de saídas; persistido |
| 15 | Vozes do Windows (SAPI/WinRT) | F, W | Opção explícita ao lado dos perfis clonados. | Mantida como opção explícita e rotulada; nunca substitui o XTTS silenciosamente. | ✅ implementado — Opção "Voz do Windows" explícita; erro XTTS nunca cai para ela |
| 16 | Mensagens de erro | F, W, S | Mensagens em português; algumas genéricas. | Serviço devolve `code` + `message` + `action`; widget exibe ação sugerida. | ✅ implementado — `ServiceError(code, action)`; widget mostra `message` + `action` |
| 17 | Indicadores de carregamento | F (status + mensagem do serviço), W (texto de status) | Flutter faz polling de `/health` durante a geração. | Widget faz polling de `/health` (mensagem + progresso) durante jobs. | ✅ implementado — Polling de `ping` a cada 1 s durante jobs (barra de progresso) |
| 18 | Velocidade / tom / volume | F | Velocidade aplica ao XTTS (0,5–1,5); tom só nas vozes Windows. | Widget: velocidade e volume no painel de ajustes. Tom fora de escopo (não se aplica ao XTTS). | ✅ implementado — Velocidade (0,5–1,5) e volume no painel Ajustes |
| 19 | Modo de processamento (auto/GPU/CPU) | F (configurações), S (`POST /config`) | `auto`, `gpu`(CUDA), `cpu`. | `auto`, `cuda`, `directml`, `cpu` (`rocm` reservado); tela de diagnóstico. | ✅ implementado — `POST /config` com backends; painel Diagnóstico |
| 20 | Atalhos de teclado | F (`Enter`, `Esc`, hotkey global de visibilidade), W (`Enter`, `Esc`) | Hotkey global é da janela Flutter. | Widget: `Enter` fala, `Esc` cancela/para. Visibilidade é gerida pela própria Game Bar (`Win+G`). | ✅ implementado — `Enter`/`Esc`; sem hotkey própria (Game Bar gere) |
| 21 | Persistência de preferências | F (`shared_preferences`), W (`LocalSettings`), S (`config.json`) | Compute mode em `config.json` (compartilhado). | `LocalSettings` do widget + `config.json` do serviço. | ✅ implementado |
| 22 | Inicialização/encerramento do serviço | F (processo filho), B (processo filho do bridge) | Encerrado quando o app/bridge fecha. Modelo recarregado a cada abertura. | Serviço independente com arquivo de descoberta, instância única por usuário, encerramento por inatividade; reutilizado ao reabrir o widget. | ✅ implementado — `service.json` + mutex + `--idle-timeout`; bridge reutiliza instância |
| 23 | Fechar e reabrir o Game Bar | W | Página é descarregada/recarregada; áudio continua disponível. | Igual; serviço permanece vivo por inatividade configurável. | ✅ implementado |
| 24 | Queda do serviço | F (mensagem), W (relança o bridge) | Bridge relançado ao próximo comando. | Bridge relança o serviço; widget mostra estado real e botão "Reconectar". | ✅ implementado — `EnsureStartedAsync` relança; botão Reconectar |
| 25 | Download do modelo XTTS | S (via `TTS.api`) | Baixado pelo Coqui no primeiro uso, sem progresso. | Serviço baixa com SHA-256 e progresso (`POST /model/ensure`); instalador executa antes do primeiro uso. | ✅ implementado — `model.py` com SHA-256 pinado; instalador chama `ensure-model` |
| 26 | Guia para o Discord | F (painel) | Instruções + botão Mixer de volume. | Widget: texto curto no painel Ajustes (`CABLE Output` como microfone). | ✅ implementado — Painel Ajustes |
| 27 | Licenças/atribuições | Instalador (Inno) | Avisos VB-CABLE, XTTS, FFmpeg. | Instalador standalone mantém os avisos. | ✅ implementado — `installer/licenses/*` copiados; VB-CABLE identificado no assistente |
| 28 | Instalação do VB-CABLE | Instalador (Inno) | `VBCABLE_Setup_x64.exe -i -h` quando ausente. | Instalador standalone: detecção (ausente/instalado/reinício pendente), assinatura, código de saída, reparo. | ✅ implementado — `SVoice.Setup vbcable` (detecção + instalação + verificação) |

## Código reaproveitável

| Origem | Destino | Observação |
| --- | --- | --- |
| `python_service/service.py` (registro de perfis, corte com ffmpeg, síntese, HTTP loopback, testes) | `service/svoice_xtts/*.py` | Reestruturado em módulos: `backends`, `model`, `profiles`, `jobs`, `api`. |
| `gamebar/SVoice.GameBarBridge/Program.cs` (AppService, pipe, host HTTP, ACLs) | `gamebar/SVoice.GameBarBridge` | Mantido; ganha descoberta do serviço, cancelamento, diagnóstico, protocolo v2. |
| `gamebar/SVoice.GameBar/*` (widget, `XttsBridgeClient`) | Mesmo projeto | Ampliado com histórico, painéis de vozes/ajustes/diagnóstico e cancelamento. |
| `installer/SVoice.iss`, `installer/vendor/VBCABLE`, `installer/licenses` | `installer/` | Reescrito para instalar widget + serviço + runtime + VB-CABLE, sem Flutter. |
| `lib/app_controller.dart` (regras de histórico, seleção de dispositivos, fallback) | Referência | Comportamento reimplementado em C#. |

## Código exclusivo do Flutter (removido na Fase 10)

`lib/`, `test/`, `windows/`, `packages/flutter_tts`, `packages/hotkey_manager_windows`,
`pubspec.yaml`, `pubspec.lock`, `analysis_options.yaml`, `.metadata`,
`svoice.iml`, `.flutter-plugins-dependencies`, `.idea/`,
`installer/SVoice-flutter-legacy.iss.txt` e os trechos legados do `README.md`.

Remoção concluída após os testes reais de instalação limpa, atualização,
desinstalação com preservação de dados, síntese CUDA e ativação do widget.

## Dados a migrar

| Dado | Local atual | Ação |
| --- | --- | --- |
| Perfis clonados (`profiles.json`, `voices/<id>/reference_*.wav`, `conditioning.pt`) | `%LOCALAPPDATA%\SVoice\XTTS` | Mantidos no mesmo local. Migração adiciona `schema_version`, valida referências e faz backup de `profiles.json` antes de reescrever. |
| Modelo XTTS v2 | `%ProgramData%\SVoice\models\tts\...` | Compartilhado com o processo da Game Bar; verificado por SHA-256; instalações antigas em `%LOCALAPPDATA%` são migradas sem apagar a cópia original. |
| `config.json` (`compute_mode`: `auto`/`gpu`/`cpu`) | `%LOCALAPPDATA%\SVoice\XTTS` | `gpu` → `cuda`; demais mantidos; chaves desconhecidas preservadas. |
| Preferências do Flutter (`shared_preferences`) | Perfil do usuário | Não migradas (voz/eco/volume são reconfigurados no widget; não são dados do usuário). |
| Preferências do widget (`LocalSettings`) | Pacote MSIX | Mantidas entre atualizações do MSIX. |
