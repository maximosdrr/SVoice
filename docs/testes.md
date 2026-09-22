# Testes executados — SVoice standalone

Máquina de validação: Windows 11 Pro 10.0.26200 x64, NVIDIA GeForce RTX 3070
(driver 610.88), 12 threads de CPU, Xbox Game Bar 7.326.8061.0, VB-CABLE já
instalado, usuário `Hiran Junior` (caminho de perfil com espaço). Nenhuma GPU
AMD estava disponível; ver limitações no fim. Datas: 2026-09-21.

Legenda: ✅ executado e aprovado · ⚠️ executado com ressalva · ⏳ não executado.

## Regressão automatizada

| Teste | Comando | Resultado |
| --- | --- | --- |
| Testes do serviço (registro, migração, recuperação de backup, validação de áudio, jobs, chunking, seleção de runtime, modelo, config, corrida de descoberta, caches do launcher) | `"C:\Program Files\SVoice\runtime\python\python.exe" service\tools\run_tests.py` | ✅ 45 testes OK |
| Compilação do bridge, do widget (Debug e Release + MSIX) e do helper | `.\gamebar\build-widget.ps1`, `dotnet build installer\SVoice.Setup` | ✅ |
| Compilação limpa sem Flutter | `git worktree` novo com `flutter` removido do `PATH` → `.\installer\build-installer.ps1 -SkipRuntimeBuild` | ✅ `SVoice-Setup-2.0.4.exe` gerado a partir do checkout limpo |
| Sintaxe dos scripts PowerShell | `[Parser]::ParseFile` | ✅ |
| Autoteste do runtime empacotado (Python embutido) por pack | `python.exe svoice_xtts_service.py --self-test --torch-pack …` | ✅ torch-cpu, torch-cuda, torch-directml |
| Análise de dependências | `manifest.json` do runtime lista versões e SHA-256 de cada pack; `installed.json` no destino | ✅ |
| Verificação de segredos | `git ls-files` + grep de chaves/certificados/tokens | ✅ nenhum segredo; `.cer` público não versionado |
| Referências residuais ao Flutter | `git grep -i flutter` após a remoção | ✅ apenas menções descritivas em docs/comentários |

## Serviço XTTS — ponta a ponta (`service/tools/service_client.py`)

| Cenário | Evidência |
| --- | --- |
| Validação completa CUDA (carregar modelo, síntese curta/longa/consecutiva, cancelamento) | ✅ carregar 16 s, curta 2,2 s (4,3 s de áudio), longa 7,3 s, consecutiva 1,6 s, cancelamento OK, VRAM de pico 2,1 GB |
| Validação completa CPU | ✅ curta 9,4 s, longa 35 s, cancelamento 24 s, working set 3,3 GB |
| Validação completa DirectML (adaptador DX12 = RTX 3070) incl. comparação com CPU | ✅ curta 4,2 s, longa 15,5 s, cancelamento OK, razão de duração GPU/CPU 1,0 |
| Fallback automático DirectML → CPU quando uma operação falha (antes da correção do `inference_mode`) | ✅ síntese concluída em CPU com motivo registrado em `backend_validation` |
| Criação de perfil a partir de WAV, síntese com perfil clonado, cancelamento via `/jobs/cancel` (HTTP 499), renomear, excluir | ✅ nos três backends, com o runtime empacotado |
| Migração do registro legado (`schema_version` 1 → 2) com backup; idempotência | ✅ `backups/profiles-<data>.json`; perfil "Voice 1" (90 trechos) preservado em todas as reinstalações |
| Migração do modelo do perfil do usuário para `%ProgramData%\SVoice\models` com SHA-256 | ✅ 19:17 (`.svoice-verified.json`) |
| Instância única: segunda instância espera a descoberta e devolve o endpoint (código 3); bridge e helper adotam | ✅ |
| Texto ≥ 203 caracteres (falha latente do baseline por spaCy) | ✅ divisão própria por sentenças |

## Widget

| Cenário | Evidência |
| --- | --- |
| Janela fora da Game Bar: conexão ao bridge, serviço iniciado, perfis carregados, síntese CUDA, estados GERANDO → FALANDO → PRONTO | ✅ `gamebar.log` 15:09–15:12, capturas de tela |
| Áudio chega ao `CABLE Input` e sai em `CABLE Output` | ✅ gravação de `CABLE Output` via FFmpeg durante a fala: ~3 s com RMS 0,09–0,13 |
| Abertura dentro da Xbox Game Bar (`GameBarContext=True`) com serviço instalado, `state=ready`, `model_ready=True`, perfil listado | ✅ 19:35, 19:52, 19:59, 20:07 |
| Reabertura do widget reutilizando o serviço já em execução (sem recarregar o modelo) | ✅ 20:07 (bridge de 19:59 adotado) |
| Clonagem de dentro da Game Bar com 2.0.3 | ⚠️ perfil criado 20:07:42; o carregamento do modelo travou no cache do numba (processo com identidade do pacote gravando em Program Files). Corrigido em 2.0.4 (`_configure_caches`) |
| Clonagem e síntese de dentro da Game Bar com 2.0.4 (usuário) | ✅ 20:32:48 widget aberto (`GameBarContext=True`, 3 perfis); 20:33:28 perfil criado (2 trechos, 20,1 s); modelo carregado em 25,2 s; 20:34:00 `backend=cuda`, 4 perfis; sínteses às 20:34:18 e 20:34:27 com o perfil novo |
| Cancelamento (Esc), histórico e Eco dentro da Game Bar | ⚠️ exercitados pelo usuário sem registro em log (funções verificadas na janela standalone, mesmo código) |

## Widget 2.1.0 — redesenho focado no chat

| Cenário | Evidência |
| --- | --- |
| Compilação Debug e Release + MSIX assinado 2.1.0 (`build-widget-package.ps1`) | ✅ 21:05 |
| Janela standalone: chat em balões (mais antigo no topo, rolagem automática), chip VOZ, aba Vozes (lista com Voz do Windows, CLONAR VOZ, renomear, excluir), aba Ajustes com seção Diagnóstico, modo compacto escondendo o histórico | ✅ capturas de tela `design-2`, `design-voices`, `design-settings`, `design-compact` |
| Dentro da Xbox Game Bar: pacote assinado 2.1.0 registrado sobre o 2.0.4 com dados preservados (`Remove-AppxPackage -PreserveApplicationData` + `Add-AppxPackage`), widget aberto com `GameBarContext=True`, `state=ready`, `backend=cuda`, 5 perfis | ✅ `gamebar.log` 21:05:23 e 21:05:37 |
| Modo compacto dentro da Game Bar: widget reaberto já compacto (estado lembrado) e redimensionado pela `TryResizeWindowAsync` para cabeçalho + caixa de texto, sem erro `Window resize failed` no log | ✅ captura `gb-2` 21:06 |
| Expandir/abrir abas e sintetizar dentro da Game Bar com o 2.1.0 | ⏳ não automatizado (a máquina estava em uso pelo usuário durante o teste); mesmo código verificado na janela standalone |
| Testes do serviço após a mudança de `SERVICE_VERSION` | ✅ 45 testes OK |

## Widget 2.1.1 — reprodução com a Game Bar oculta

| Cenário | Evidência |
| --- | --- |
| Testes do serviço após a mudança de versão | ✅ 45 testes OK; 7 testes opcionais de importação com FFmpeg ignorados no Python de desenvolvimento |
| Compilação Debug e Release + MSIX assinado 2.1.1 | ✅ `build-widget.ps1` e `build-widget-package.ps1` |
| Manifesto instalado expõe `backgroundMediaPlayback` | ✅ confirmado por `Get-AppxPackageManifest` |
| MSIX assinado e registrado; ativação do widget na Game Bar | ✅ assinatura válida; `GameBarContext=True`, bridge conectado, serviço pronto, 5 perfis |
| Atividade de fala sobrevive ao `Page.Unloaded` e só termina no fim/cancelamento da mídia | ✅ caminho de ciclo de vida corrigido e compilado; registros de início/fim adicionados |
| Confirmação auditiva digitando, ocultando com Win+G durante a síntese e aguardando a fala | ⏳ requer interação manual porque a automação desta sessão não recebeu acesso às janelas nativas |

## Widget 2.1.2 — síntese iniciada antes de ocultar a Game Bar

| Cenário | Evidência |
| --- | --- |
| Sessão silenciosa iniciada no mesmo `MediaPlayer` antes da chamada XTTS e substituída pela fala pronta | ✅ implementação compilada em Debug e Release |
| Testes do serviço | ✅ 45 testes OK; 7 opcionais de FFmpeg ignorados no ambiente de desenvolvimento |
| MSIX 2.1.2 assinado, instalado e ativado dentro da Game Bar | ✅ pacote com status `Ok`; XTTS/CUDA pronto e perfil carregado |
| Instalador offline completo 2.1.2 | ✅ gerado e SHA-256 confirmado |
| Confirmação auditiva com Enter → Win+G imediatamente, ainda em PREPARANDO/GERANDO | ⏳ validação manual do usuário |

## Widget 2.1.3 — instalação limpa e pacote autocontido

| Cenário | Evidência |
| --- | --- |
| MSIX contém o runtime .NET 10 necessário ao widget | ✅ `coreclr.dll`, `hostfxr.dll` e `System.Private.CoreLib.dll` presentes na raiz do pacote |
| Build rejeita regressão para pacote dependente de framework | ✅ `build-widget.ps1` valida as três bibliotecas obrigatórias dentro do MSIX |
| Pré-requisito da Game Bar | ✅ versão instalada 7.326.8061.0 aceita; versões anteriores a 6.124.0122.0 são bloqueadas com orientação de atualização |
| Atualização 2.1.2 → 2.1.3 | ✅ pacote assinado instalado, catálogo atualizado e versão anterior substituída |
| Ativação de diagnóstico sem janela | ✅ `runtime e XAML inicializados` pelo novo `--startup-probe` |
| Verificação pós-instalação completa | ✅ widget 2.1.3, VB-CABLE, runtime CUDA, modelo e último teste de síntese confirmados |
| Testes do serviço | ✅ 45 testes OK; 7 opcionais de FFmpeg ignorados no ambiente de desenvolvimento |
| Instalação e abertura visual em Windows limpo sem .NET/Visual Studio | ⏳ validação manual do usuário |

## Instalador (`SVoice.Setup` + Inno Setup)

| Cenário | Evidência |
| --- | --- |
| `check`, `detect-gpu` → `torch-cuda`, `vbcable` (registro + dispositivos + assinatura Authenticode do pacote oficial) | ✅ |
| `install-runtime` offline com SHA-256 e extração atômica; `ensure-model` reutilizando/migrando o modelo; `test-service` | ✅ `install-diagnostics.json` 19:58: CUDA, síntese curta 4,8 s |
| Primeira instalação (2.0.1) substituindo o SVoice Flutter 1.4.7 | ✅ 18:44 — legado removido, `Program Files\SVoice` novo, perfil e modelo preservados |
| Atualização 2.0.1 → 2.0.2 → 2.0.3 → 2.0.4 sem perda de perfis/modelo | ✅ 19:00, 19:23, 19:45, 19:58, 20:24; "Verification (post-install): ok" em todas; teste CUDA do instalador 2.0.4 às 20:24:41 |
| Desinstalação preservando dados + reinstalação limpa (runtime removido pelo desinstalador e reextraído) | ✅ 19:08 → 19:16 e 19:21 → 19:23 (`Widget removed` → `Runtime pack installed` ×3), `profiles.json` intacto |
| Desinstalação removendo dados mediante confirmação | ⏳ não executado (dados do usuário mantidos propositalmente nesta máquina) |
| Reparo (`Reparar SVoice`) | ⏳ |
| Reinicialização do Windows e reabertura | ⏳ |
| VB-CABLE ausente / reinicialização pendente | ⏳ não há máquina sem VB-CABLE disponível; detecção de reboot pendente verificada (`reboot_pending=true` nesta máquina) |
| AMD DirectML real / AMD sem suporte / máquina sem GPU dedicada | ⏳ sem hardware; a validação automática no destino decide o backend |

## Defeitos encontrados e corrigidos durante os testes

| Defeito | Correção |
| --- | --- |
| `split_sentence` do coqui-tts exige spaCy (textos ≥ 203 caracteres falhavam) | divisor próprio por sentenças (`split_into_chunks`) |
| DirectML: "Cannot set version_counter for inference tensor" | `Xtts.inference` sob `torch.no_grad` no backend DirectML |
| Widget: `TextBox` criado fora da thread de UI após o `FileOpenPicker` | criação/exibição do diálogo via dispatcher |
| Widget: deadlock ao resolver o dispositivo de áudio na thread de UI | `ApplyOutputDeviceAsync` |
| Widget: `Slider.Minimum` em XAML falhava na análise em runtime | limites definidos em código |
| Corrida bridge × instalador (mutex sem descoberta publicada) | serviço espera a descoberta; bridge/helper adotam; MSIX registrado só após a validação |
| Cache do numba em Program Files trava a importação no processo do pacote | caches redirecionados para `%LOCALAPPDATA%\SVoice\Cache` |
