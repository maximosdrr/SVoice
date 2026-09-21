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
| Clonagem iniciada de dentro da Game Bar (perfil criado, condicionamento) | ⚠️ perfil criado 20:07:42; o carregamento do modelo travou no cache do numba (processo com identidade do pacote gravando em Program Files). Corrigido em 2.0.4 (`_configure_caches`); **reteste pendente com o 2.0.4** |
| Síntese, cancelamento (Esc), histórico e Eco dentro da Game Bar | ⏳ pendente com o 2.0.4 (funções verificadas na janela standalone, mesmo código) |

## Instalador (`SVoice.Setup` + Inno Setup)

| Cenário | Evidência |
| --- | --- |
| `check`, `detect-gpu` → `torch-cuda`, `vbcable` (registro + dispositivos + assinatura Authenticode do pacote oficial) | ✅ |
| `install-runtime` offline com SHA-256 e extração atômica; `ensure-model` reutilizando/migrando o modelo; `test-service` | ✅ `install-diagnostics.json` 19:58: CUDA, síntese curta 4,8 s |
| Primeira instalação (2.0.1) substituindo o SVoice Flutter 1.4.7 | ✅ 18:44 — legado removido, `Program Files\SVoice` novo, perfil e modelo preservados |
| Atualização 2.0.1 → 2.0.2 → 2.0.3 sem perda de perfis/modelo | ✅ 19:00, 19:23, 19:45, 19:58; "Verification (post-install): ok" em todas |
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
