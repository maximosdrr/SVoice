# Changelog

## 2.0.4 — 2026-09-21

Primeira versão standalone: o SVoice passa a existir apenas como widget da
Xbox Game Bar com serviço XTTS v2 local. O aplicativo Flutter foi removido.

### Adicionado
- Widget Xbox Game Bar com clonagem de voz, síntese XTTS v2, cancelamento
  (`Esc`), histórico com repetição, painéis de vozes (renomear/excluir),
  ajustes (processamento, saída de áudio, velocidade, volume) e diagnóstico
  (GPU, backend ativo, motivo de fallback, teste de backend, modelo, reconexão).
- Serviço XTTS standalone (`service/`): API HTTP loopback v2, instância única
  por usuário com arquivo de descoberta, encerramento por inatividade, logs com
  rotação, jobs canceláveis, migração do registro de perfis com backup e
  recuperação, verificação/download do modelo por SHA-256.
- Backends de inferência: NVIDIA CUDA 13, AMD/Intel DirectML (experimental,
  ativado apenas após validação completa), CPU; ROCm reservado.
- Runtime modular (Python embutido + packs `base`, `torch-cpu`, `torch-cuda`,
  `torch-directml`) gerado por `service\runtime\build-runtime.ps1`.
- Instalador único (Inno Setup + `SVoice.Setup`): pré-requisitos, remoção da
  versão Flutter, certificado, VB-CABLE oficial com verificação de assinatura e
  reinicialização, runtime por hardware, modelo compartilhado em `%ProgramData%`,
  teste de síntese, verificação final e pós-reinicialização, reparo,
  desinstalação com perguntas separadas para perfis e modelo.
- Documentação: instalação e uso, backends, segurança, solução de problemas,
  testes executados, matriz de paridade.

### Corrigido
- Textos longos falhavam por dependência do spaCy.
- Diálogo de clonagem fora da thread de UI; deadlock ao escolher a saída de
  áudio; parse de XAML do `Slider`.
- Corrida entre o widget aberto e o instalador ao iniciar o serviço.
- Travamento na primeira importação da librosa no processo do pacote (cache
  do numba em Program Files).

### Removido
- Aplicativo desktop Flutter, plugins modificados, serviço PyInstaller e
  instalador da era Flutter (histórico nas tags `svoice-pre-standalone` e
  `svoice-pre-flutter-removal`).

## 1.4.7 — 2026-09-20

Última versão do aplicativo Flutter (overlay desktop) com widget 0.3.x.
