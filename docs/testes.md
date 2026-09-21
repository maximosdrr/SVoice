# Testes executados — SVoice standalone

Máquina de validação: Windows 11 Pro 10.0.26200 x64, NVIDIA GeForce RTX 3070
(driver 610.88), 12 threads de CPU, Xbox Game Bar 7.326.8061.0, VB-CABLE já
instalado, usuário `Hiran Junior` (caminho de perfil com espaço). Nenhuma GPU
AMD estava disponível; ver limitações no fim.

Legenda: ✅ executado e aprovado · ⚠️ executado com ressalva · ⏳ pendente/não executado.

## Regressão automatizada

| Teste | Comando | Resultado |
| --- | --- | --- |
| Testes do serviço (registro, migração, recuperação de backup, validação de áudio, jobs, chunking, seleção de runtime, modelo, config) | `python -m unittest discover -s service/tests` | ✅ 40 testes OK (2026-09-21) |
| Compilação do bridge | `dotnet build gamebar/SVoice.GameBarBridge` | ✅ |
| Compilação do widget Debug e Release + MSIX | `.\gamebar\build-widget.ps1 -Configuration Debug/Release` | ✅ (`SVoice.GameBar_2.0.3.0_x64.msix`) |
| Compilação do helper | `dotnet build installer/SVoice.Setup` | ✅ |
| Sintaxe dos scripts PowerShell | `[Parser]::ParseFile` | ✅ |
| Autoteste do runtime empacotado (Python embutido) por pack | `python.exe svoice_xtts_service.py --self-test --torch-pack …` | ✅ torch-cpu, torch-cuda, torch-directml |
| Verificação de segredos no repositório | `git ls-files` + grep de chaves/certificados | ✅ nenhum segredo; `.cer` público não versionado |

## Serviço XTTS — ponta a ponta (`service/tools/service_client.py`)

| Cenário | Evidência |
| --- | --- |
| Validação completa CUDA (carregar modelo, síntese curta/longa/consecutiva, cancelamento) | ✅ carregar 16 s, curta 2,2 s (4,3 s de áudio), longa 7,3 s, consecutiva 1,6 s, cancelamento OK, VRAM de pico 2,1 GB |
| Validação completa CPU | ✅ curta 9,4 s, longa 35 s, cancelamento 24 s, working set 3,3 GB |
| Validação completa DirectML (adaptador DX12 = RTX 3070) incl. comparação com CPU | ✅ curta 4,2 s, longa 15,5 s, cancelamento OK, razão de duração GPU/CPU 1,0 |
| Fallback automático DirectML → CPU quando uma operação falha (antes da correção do `inference_mode`) | ✅ síntese concluída em CPU com motivo registrado em `backend_validation` |
| Criação de perfil a partir de WAV, síntese com perfil clonado, cancelamento via `/jobs/cancel` (HTTP 499), renomear, excluir | ✅ nos três backends |
| Migração do registro legado (`schema_version` 1 → 2) com backup | ✅ backup em `backups/profiles-<data>.json`; idempotente na segunda execução |
| Instância única: segunda instância espera a descoberta e devolve o endpoint existente (código 3) | ✅ |
| Texto ≥ 203 caracteres (falha latente do baseline por spaCy) | ✅ divisão própria por sentenças |

## Widget

| Cenário | Evidência |
| --- | --- |
| Abertura como janela (fora da Game Bar), conexão ao bridge, serviço iniciado, perfis carregados | ✅ `gamebar.log` 15:09 |
| Síntese com perfil clonado, estado GERANDO → FALANDO → PRONTO, badge `NVIDIA CUDA` | ✅ capturas `widget-3`, `widget-4` |
| Áudio chega ao `CABLE Input` e sai em `CABLE Output` | ✅ gravação de `CABLE Output` via FFmpeg durante a fala: ~3 s com RMS 0,09–0,13 |
| Abertura dentro da Xbox Game Bar (contexto real) | ⚠️ widget carregou (`GameBarContext=True`, 18:50) mas o serviço iniciado pelo instalador segurava o mutex sem descoberta publicada → "XTTS INDISPONÍVEL". Corrigido (espera pela descoberta no serviço e no bridge); reteste pendente abaixo |

## Instalador (helper `SVoice.Setup`)

| Cenário | Evidência |
| --- | --- |
| `check` (Windows, arquitetura, Game Bar, GPU, disco, caminho) | ✅ |
| `detect-gpu` → `torch-cuda` | ✅ |
| `vbcable` detecção (registro + dispositivos) e assinatura Authenticode do pacote oficial | ✅ assinatura válida (Vincent Burel / VB-Audio) |
| `install-runtime` offline a partir dos zips com SHA-256, extração atômica, `installed.json` | ✅ |
| `ensure-model` reutilizando o modelo existente (verificação SHA-256 de 1,9 GB) | ✅ |
| `test-service` (validação CUDA pelo runtime instalado) | ✅ `install-diagnostics.json` 18:52: curta 4,8 s |
| Primeira instalação real do `SVoice-Setup-2.0.1.exe` nesta máquina (substituindo o SVoice Flutter 1.4.7) | ✅ `Program Files\SVoice` com runtime `torch-cuda`, widget 2.0.1.0 registrado, "Verification (post-install): ok" |

## Pendentes (executar com o instalador 2.0.3)

| Cenário | Estado |
| --- | --- |
| Atualização 2.0.2 → 2.0.3 com migração do modelo para ProgramData, sem perda de perfis | ⏳ |
| Uso real na Game Bar: abrir, clonar, sintetizar, cancelar, histórico, Eco | ⏳ |
| Reparo (`Reparar SVoice`) | ⏳ |
| Desinstalação preservando dados / removendo dados | ⏳ |
| Reinicialização do Windows e reabertura | ⏳ |
| VB-CABLE ausente (máquina sem driver) | ⏳ não há máquina sem VB-CABLE disponível |
| AMD DirectML / AMD sem suporte / máquina sem GPU dedicada | ⏳ sem hardware disponível; validação automática no destino |
