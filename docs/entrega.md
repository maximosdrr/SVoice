# Entrega — SVoice 2.1.1 (Xbox Game Bar standalone)

## Artefatos

Gerados em 2026-09-22 a partir da árvore de trabalho da correção de reprodução
em segundo plano (`installer\build-installer.ps1 -SkipWidgetBuild -SkipRuntimeBuild`).
Os artefatos ficam em `artifacts\` (ignorado pelo Git) e não são versionados;
os hashes abaixo permitem conferir o que foi distribuído.

| Arquivo | Tamanho | SHA-256 |
| --- | --- | --- |
| `SVoice-Setup-2.1.1.exe` (instalador único, offline, todos os packs) | 2 882 910 919 bytes | `6e661fcc14f08d01b0a7ea3441623169960a70d5f832cfc4685f8836bb7a126a` |
| `widget/SVoice.GameBar_2.1.1.0_x64.msix` (dentro do instalador) | 49 052 146 bytes | `bb8cafdd0d9f7d8698c120cb7fe39c7e40d0034bda5f1fa446ab67c864bc1ec0` |
| `runtime/svoice-python-3.12.10.zip` | 11 945 116 bytes | `ae9071ff4b26ec8b4738febfbda093887a98700ecfc200e9c737e42d918b52d2` |
| `runtime/svoice-pack-base-2026.09.21.zip` | 199 993 354 bytes | `f12b7fe5f6e5d0bcbe6399795bf404b23c4de355bd2cc62e62f978d4d01dee18` |
| `runtime/svoice-pack-torch-cpu-2026.09.21.zip` | 131 902 593 bytes | `18149bc693efd66abcdb104c31b586fe546e10fe4eb5f433f024e30b0a0f7056` |
| `runtime/svoice-pack-torch-cuda-2026.09.21.zip` | 2 008 146 233 bytes | `b3b0b017a9b448a226f83685420642c4667e98aff4db45a6c087430eaba8e32f` |
| `runtime/svoice-pack-torch-directml-2026.09.21.zip` | 447 268 678 bytes | `f88f69bea8a24b6fab8bfbdbedfa6cd45825e7eb6967b101136531701d7fa618` |

O manifesto versionado `service\runtime\manifest.json` contém os mesmos hashes
e é usado pelo helper para verificar cada pack antes de extraí-lo.

## Documentação

| Assunto | Documento |
| --- | --- |
| Instalação, atualização, reparo e desinstalação | [instalacao-e-uso.md](instalacao-e-uso.md) |
| Diagnóstico de GPU e explicação dos backends NVIDIA, AMD e CPU | [backends.md](backends.md) |
| Segurança e confiabilidade | [seguranca.md](seguranca.md) |
| Problemas frequentes | [solucao-de-problemas.md](solucao-de-problemas.md) |
| Testes realmente executados e defeitos corrigidos | [testes.md](testes.md) |
| Paridade com o aplicativo Flutter | [matriz-de-paridade.md](matriz-de-paridade.md) |
| Versão e changelog | [../CHANGELOG.md](../CHANGELOG.md) |
| Estado anterior à migração | [baseline-pre-standalone.md](baseline-pre-standalone.md) |

## VB-CABLE — aviso e atribuição

O instalador contém o pacote oficial e inalterado **VB-CABLE Driver Pack 45**
da **VB-Audio Software** (Vincent Burel), verificado por assinatura Authenticode
antes da instalação. VB-CABLE é *donationware*: origem <https://vb-cable.com>,
contribuições e licenciamento em <https://vb-audio.com/Cable/> e
<https://vb-audio.com/Services/licensing.htm>. A redistribuição segue as
condições publicadas pela VB-Audio (usuário final identifica o VB-CABLE como
produto da VB-Audio e pode doar/licenciar); apenas o pacote base é incluído
(A+B e C+D não são redistribuídos). O aviso é exibido no assistente de
instalação (`InfoBeforeFile`) e mantido em `licenses\VB-CABLE-NOTICE.txt`.
**Distribuição comercial ou em volume exige licenciamento junto à VB-Audio antes
da publicação.**

Demais componentes: XTTS v2 (Coqui Public Model License 1.0.0, uso não
comercial), coqui-tts (MPL 2.0), PyTorch (BSD), torch-directml (MIT), FFmpeg
via imageio-ffmpeg (GPL v3, processo separado), Python (PSF).

## Confirmação: o Flutter não é necessário

- Nenhum componente instalado depende do Flutter: widget (.NET 10 UWP),
  bridge (.NET 10), serviço (Python embutido + packs), helper (.NET 10
  auto-contido), Inno Setup.
- O código Flutter foi removido do branch em `refactor: remove obsolete
  Flutter application`; `git grep -i flutter` só encontra menções descritivas.
- A compilação completa foi reproduzida em um checkout limpo com o Flutter
  fora do `PATH`.
- Tags de segurança: `svoice-pre-standalone` (1.4.7 + widget 0.3.1) e
  `svoice-pre-flutter-removal` (2.0.3 com o Flutter ainda no branch).

## Limitações conhecidas

1. **AMD**: o backend DirectML foi validado apenas em um adaptador DirectX 12
   NVIDIA (RTX 3070 via DirectML). Em GPUs AMD reais a aceleração só é
   anunciada se a matriz automática passar na máquina do usuário; caso
   contrário o serviço usa CPU e registra o motivo. Não há pack ROCm (a prévia
   oficial da AMD cobre poucas GPUs e não foi validada).
2. **NVIDIA anteriores a Turing** (GTX 10xx/9xx) não são suportadas pelo
   CUDA 13 e usam CPU; driver mínimo 580.
3. **Instalador offline de 2,7 GB**: sem hospedagem de release publicada, o
   modo por download (`download_base_url`) fica pronto mas inativo.
4. **Certificado de desenvolvimento**: o MSIX é assinado por
   `CN=SVoice Development`, instalado em *Pessoas confiáveis* pelo instalador.
   Uma assinatura pública confiável dispensaria essa etapa.
5. **Testes não executados nesta máquina** (ver testes.md): desinstalação com
   exclusão de dados, reparo, reinicialização do Windows, VB-CABLE ausente,
   AMD real e máquina sem GPU dedicada. Clonagem e síntese dentro da Game
   Bar foram validadas com o 2.0.4; no 2.1.0 (redesenho) foi validada a
   abertura e o modo compacto dentro da Game Bar, e o restante na janela
   standalone (mesmo código). No 2.1.1 foram validados o pacote, a capacidade
   de áudio em segundo plano e a ativação dentro da Game Bar; a confirmação
   auditiva após ocultar a sobreposição continua sendo manual.
6. **Modelo XTTS v2**: 1,9 GB baixados de `huggingface.co/coqui/XTTS-v2` na
   instalação (ou fornecidos ao lado do instalador); a primeira fala após
   abrir o widget carrega o modelo (10–30 s em GPU).
7. **Idioma**: a síntese usa `pt` (português); outros idiomas do XTTS não
   estão expostos na interface.
