# Baseline antes da migração standalone

Data: 2026-09-21. Tag Git: `svoice-pre-standalone`.

Este documento registra o estado funcional do SVoice imediatamente antes da
migração para a solução standalone baseada apenas no Xbox Game Bar. Ele serve
como referência de paridade e como ponto de retorno seguro.

## Componentes existentes

| Componente | Local | Estado |
| --- | --- | --- |
| Aplicativo Flutter (overlay desktop) | `lib/`, `windows/`, `packages/`, `test/` | Funcional. Versão 1.4.7. |
| Serviço XTTS v2 (Python) | `python_service/service.py` | Funcional. Versão 1.1.3. HTTP em `127.0.0.1`, porta aleatória, token Bearer. |
| Widget Xbox Game Bar (UWP, .NET 10 + CsWinRT) | `gamebar/SVoice.GameBar/` | Funcional. Versão 0.3.1.0. |
| Bridge full-trust do widget | `gamebar/SVoice.GameBarBridge/` | Funcional. Empacotado dentro do MSIX em `Bridge\`. |
| Instalador completo (Inno Setup 7) | `installer/SVoice.iss` | Funcional. Instala Flutter + `xtts_service` + VB-CABLE. |
| Scripts de build/instalação do widget | `gamebar/*.ps1` | Funcionais. |

## Como os componentes se relacionam hoje

```
Xbox Game Bar ──▶ SVoice.GameBar (UWP) ──AppService──▶ SVoice.GameBarBridge.exe (full trust, dentro do MSIX)
                                                            │
                                                            ▼ HTTP loopback + token
                                              svoice_xtts_service.exe (PyInstaller, ~3,3 GB)
                                              localizado em %ProgramFiles%\SVoice\xtts_service
                                              (instalado pelo instalador do aplicativo Flutter)
                                                            │
                                                            ▼
                                       %LOCALAPPDATA%\SVoice\XTTS  (modelo, perfis, temp, config)
```

Dependências do widget em relação ao Flutter identificadas nesta data:

1. O executável `svoice_xtts_service.exe` só é instalado pelo `SVoice-Setup`
   (Inno Setup) do aplicativo Flutter, em `%ProgramFiles%\SVoice\xtts_service`.
2. O VB-CABLE só é instalado pelo mesmo instalador.
3. O widget exibe a mensagem "Instale o SVoice completo" quando o serviço não
   é encontrado (`install-widget-package.ps1` também exige a instalação).
4. Não há dependência de processo: o widget e o bridge funcionam sem o
   `SVoice.exe` em execução.

Os perfis de voz e o modelo ficam em `%LOCALAPPDATA%\SVoice\XTTS`, fora do
diretório do MSIX; esse layout já é compartilhado pelo Flutter e pelo widget.

## Ambiente de validação

- Windows 11 Pro 10.0.26200, x64.
- Xbox Game Bar (`Microsoft.XboxGamingOverlay`) 7.326.8061.0.
- GPU NVIDIA GeForce RTX 3070 (8 GB), driver 610.88.
- VB-CABLE instalado (`VBAudioVACMME` presente; dispositivo "VB-Audio Virtual Cable" OK).
- Flutter 3.47.2 / Dart 3.13.2; .NET SDK 10.0.301; Visual Studio 18.7.2 (MSBuild 18.7.8);
  Windows SDK 10.0.26100; Python 3.12.10; Inno Setup 7.
- Perfil de usuário existente: 1 perfil clonado ("Voice 1", 90 trechos, 1800 s) e o
  modelo XTTS v2 já baixado (`model.pth` 1,87 GB).

## Testes executados nesta data

| Verificação | Comando | Resultado |
| --- | --- | --- |
| Análise Flutter | `flutter analyze` | `No issues found!` |
| Testes Flutter | `flutter test` | 16 testes, todos aprovados |
| Testes do serviço XTTS | `python -m unittest discover -s python_service/tests -v` | 15 testes, todos aprovados |
| Widget Debug + bridge | `.\gamebar\build-widget.ps1 -Configuration Debug` | MSIX gerado em `AppPackages\SVoice.GameBar_0.3.1.0_x64_Debug_Test` |
| Widget Release + bridge | `.\gamebar\build-widget.ps1 -Configuration Release` | MSIX gerado em `AppPackages\SVoice.GameBar_0.3.1.0_x64_Test` |
| Sintaxe dos scripts PowerShell | `[Parser]::ParseFile` em todos os `*.ps1` | 6 scripts sem erros |
| Estrutura do MSIX | Leitura do `AppxManifest.xml` e entradas do pacote | Identidade `SVoice.GameBar 0.3.1.0`, extensões `fullTrustProcess`, `appService` e `appExtension` presentes; bridge (100 MB) incluído |
| Widget no catálogo da Game Bar | `AppExtensionCatalog.Open("microsoft.gameBarUIExtension")` | `SVoiceWidget` registrado por `SVoice.GameBar_0.3.1.0_x64__61qvngw278t1j` |
| Uso real no Game Bar | Logs `gamebar.log` e `gamebar-bridge.log` de 2026-09-21 | Perfis carregados e sínteses XTTS executadas com sucesso pelo widget |

## Falha corrigida no baseline

O log do widget registrava `COMException 0x8001010E` em
`CloneVoiceButton_Click`: após `FileOpenPicker.PickMultipleFilesAsync()` a
continuação retornava fora da thread de UI e a criação do `TextBox` do diálogo
falhava. A construção e exibição do diálogo passaram a ocorrer via
`RunOnUiThreadAsync`. A correção compila (Debug); o fluxo de clonagem dentro do
Game Bar será revalidado na migração do widget, pois o pacote registrado na
máquina ainda é o 0.3.1.0 assinado.

## Limitações conhecidas do baseline

- Cancelamento de síntese no widget interrompe apenas a reprodução; a geração
  em andamento continua no serviço até terminar.
- O histórico do widget mostra apenas a última frase.
- Não há tela de diagnóstico de hardware no widget; o backend usado (CUDA/CPU)
  não é exibido.
- Exclusão e renomeação de perfis só existem no Flutter (exclusão) ou não
  existem (renomeação).
- Aceleração AMD não é suportada (apenas CUDA ou CPU).
- O serviço XTTS é distribuído como pacote único de ~3,3 GB com PyTorch CUDA.
