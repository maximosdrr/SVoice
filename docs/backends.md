# Backends de inferência (NVIDIA, AMD e CPU)

O serviço XTTS do SVoice escolhe o backend em duas etapas:

1. **Launcher** (`service/svoice_xtts_service.py`) — antes de importar o
   PyTorch, lê a GPU no registro do Windows, o `compute_mode` salvo pelo usuário
   e o histórico de validações, e coloca **um** pack de PyTorch no `sys.path`:
   `torch-cuda`, `torch-directml` ou `torch-cpu`.
2. **Engine** (`service/svoice_xtts/engine.py`) — dentro do processo, verifica
   quais backends o PyTorch carregado suporta, **valida** o preferido com uma
   síntese XTTS v2 completa e cai para CPU se qualquer etapa falhar. O resultado
   fica em `%LOCALAPPDATA%\SVoice\XTTS\config.json` (`backend_validation`) e
   é invalidado quando o driver, a GPU ou a versão do PyTorch mudam.

Modo padrão: **Automático** (prioridade CUDA → ROCm → DirectML → CPU). O
usuário pode forçar um backend em *Ajustes › Processamento*; o serviço
reinicia com o pack correspondente.

## Matriz de validação executada antes de anunciar um backend

| Etapa | O que verifica |
| --- | --- |
| carregar modelo | `Xtts.load_checkpoint` + `.to(device)` |
| voz interna | latentes embutidos em `speakers_xtts.pth` (dispensa áudio de referência) |
| síntese curta | frase com acentos em português; áudio ≥ 0,25 s, sem NaN/Inf, RMS > 0,005 |
| síntese longa | três sentenças (divisão por sentença sem spaCy) |
| síntese consecutiva | segunda chamada com o modelo já carregado |
| cancelamento | cancela entre sentenças e confirma `CancelledError` |
| comparação com CPU | apenas backends experimentais (DirectML): duração do áudio na GPU entre 0,5× e 2× a da CPU |
| memória | pico de VRAM (CUDA) e working set do processo registrados no relatório |

## Packs de runtime

| Pack | Conteúdo | Tamanho (zip / instalado) | Requisitos |
| --- | --- | --- | --- |
| `base` | coqui-tts 0.27.5, transformers, Silero VAD + modelo ONNX, ONNX Runtime, numpy, librosa, FFmpeg… | tamanho registrado no manifesto da versão | sempre instalado |
| `torch-cpu` | PyTorch 2.14.0+cpu, torchaudio, torchcodec | 126 MB / 0,5 GB | qualquer PC x64 |
| `torch-cuda` | PyTorch 2.14.0+cu130 (CUDA 13.0, cuDNN 9) | 1,9 GB / 3,0 GB | NVIDIA Turing (RTX 20/GTX 16) ou mais nova, driver ≥ 580 |
| `torch-directml` | PyTorch 2.4.1+cpu, torchvision 0.19.1, torch-directml 0.2.5.dev240914, MKL | 427 MB / 1,8 GB | GPU DirectX 12 (AMD, Intel ou NVIDIA); experimental |

Os packs são zips gerados por `service\runtime\build-runtime.ps1` e listados em
`service\runtime\manifest.json` com SHA-256. O instalador extrai apenas o pack
recomendado (mais `base`); `SVoice.Setup install-runtime` também aceita
download a partir de `download_base_url` com cache em
`%LOCALAPPDATA%\SVoice\Downloads` e verificação de hash.

O detector Silero e seu modelo ONNX pertencem ao pack `base` e funcionam sem
download adicional. O modelo XTTS v2 mantém seu fluxo separado: é reutilizado
quando já existe ou baixado e verificado na primeira instalação.

### Por que o instalador é "offline completo"

Foram comparadas duas distribuições:

| Opção | Prós | Contras |
| --- | --- | --- |
| Instalador pequeno + download do pack necessário | ~250 MB para baixar; sem duplicar packs | depende de hospedagem para arquivos de 1,9 GB, de conexão estável e de manter URLs válidas por versão |
| Instalador completo (todos os packs embutidos, ~2,7 GB) | funciona sem internet (exceto o modelo, que pode ser fornecido ao lado do instalador); instalação determinística e reproduzível | download inicial maior |

Como não há hospedagem de release publicada nesta entrega, a opção robusta é
o **instalador completo**; o manifesto e o helper já suportam o modo por
download quando os packs forem publicados (`download_base_url`).

## NVIDIA (CUDA)

- Pack `torch-cuda` com CUDA 13.0: suporta placas Turing (compute capability
  7.5) até Blackwell. Placas Pascal/Maxwell (GTX 10xx/9xx) **não** são
  suportadas por CUDA 13 e ficam em CPU (o teste "carregar modelo"/"síntese
  curta" falha com *no kernel image* e o motivo é registrado).
- Driver mínimo 580. Com driver mais antigo o instalador recomenda `torch-cpu`
  e sugere atualizar o driver e usar **Reparar SVoice**.
- Validado nesta entrega em uma RTX 3070 (driver 610.88): síntese curta em
  ~2 s, longa em ~7 s, cancelamento entre sentenças, VRAM de pico ~2,1 GB.

## AMD

### DirectML (experimental)

- Usa `torch-directml` (Microsoft, prévia pública) com PyTorch 2.4.1.
- Ajustes necessários no serviço (aplicados automaticamente):
  - condicionamento de voz calculado na CPU (o DirectML não implementa os
    tensores complexos de `torch.stft`);
  - `Xtts.inference` executado sob `torch.no_grad` em vez de
    `torch.inference_mode` (evita *Cannot set version_counter for inference
    tensor*);
  - `upsample_linear1d` (velocidade ≠ 1,0) cai para CPU pelo próprio DirectML.
- **Só é anunciado como ativo depois que a matriz completa passa na máquina do
  usuário.** Se uma operação não for suportada, o serviço registra o motivo,
  avisa no diagnóstico e usa a CPU sem encerrar o aplicativo.
- Estado da validação nesta entrega: a matriz completa passou em um adaptador
  DirectX 12 NVIDIA (RTX 3070 via DirectML: síntese curta 5,0 s, longa 14,6 s,
  comparação com CPU 0,96×). **Nenhuma GPU AMD estava disponível**; portanto a
  aceleração AMD deve ser considerada *não confirmada em hardware AMD* até que
  a validação automática passe em uma máquina AMD real. Até lá o comportamento
  garantido é o fallback para CPU.

### ROCm

- A AMD publica uma prévia oficial de "PyTorch on Windows" (ROCm 7.2.1,
  torch 2.9.1+rocm7.2.1, Python 3.12) apenas para Radeon RX 7900 XTX, RX 9070,
  RX 9070 XT, RX 9060 XT e AI PRO R9700, exigindo o driver 26.2.2.
- O backend `rocm` existe no serviço (`backends.py`) e seria selecionado com
  prioridade sobre o DirectML, mas **nenhum pack `torch-rocm` é distribuído**:
  a prévia não cobre a maioria das GPUs AMD e não foi validada aqui. Quando
  houver hardware compatível, basta gerar o pack com os wheels oficiais de
  `repo.radeon.com` e a mesma matriz de validação decide se ele pode ser usado.

## CPU

- Funciona em qualquer PC x64 com o pack `torch-cpu` (ou com os packs de GPU,
  que também executam em CPU).
- Um job por vez, no máximo 8 threads do PyTorch; a interface do widget nunca
  bloqueia (a síntese roda no serviço e o widget faz polling de progresso).
- Nesta máquina (CPU de 12 threads): síntese curta ~8–9 s, longa ~31–35 s.
  O widget avisa que a síntese pode ser mais lenta; o cancelamento ocorre
  entre sentenças (~150 caracteres).
