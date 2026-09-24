# Entrega — SVoice 2.2.0 (Xbox Game Bar standalone)

## Artefatos

Gerados em 2026-09-24 após a reconstrução do runtime base/CPU, dos arquivos de
runtime e do widget 2.2.0. O instalador final foi montado com
`installer\build-installer.ps1 -Version 2.2.0 -SkipWidgetBuild -SkipRuntimeBuild`.
Os artefatos ficam em `artifacts\` (ignorado pelo Git) e não são versionados;
os hashes abaixo permitem conferir o que foi distribuído.

| Arquivo | Tamanho | SHA-256 |
| --- | --- | --- |
| `SVoice-Setup-2.2.0.exe` (instalador único, offline, todos os packs) | 2 956 173 029 bytes | `ff119d8328986a59b438e709d11b82c8d69dbf629d04e5995f8207e868549dda` |
| `widget/SVoice.GameBar_2.2.0.0_x64.msix` (dentro do instalador) | 84 706 547 bytes | `2d9fc68beeae9a37a70e92b7967e4f7e8bc67bc42c2f3d9b8e6cd9c562421aed` |
| `runtime/svoice-python-3.12.10.zip` | 11 945 103 bytes | `f9dfb8fab929c766734d92243dd1d4caed783d0cbb648fd07df39f1d5af03cbe` |
| `runtime/svoice-pack-base-2026.09.24.zip` | 231 839 545 bytes | `2980adb58add7561b5a526ea700a50dfea09ddbdbd4ade91f8258988899bfe4d` |
| `runtime/svoice-pack-torch-cpu-2026.09.24.zip` | 137 830 477 bytes | `39ca138956f719d96f146bfea24bc289f2d26523dc6e347a0b0aa3ffac8f3fab` |
| `runtime/svoice-pack-torch-cuda-2026.09.24.zip` | 2 008 146 233 bytes | `1afec4663856b315fc1c52663f83722aa4bbe4a57977389d05a31da0c53273c5` |
| `runtime/svoice-pack-torch-directml-2026.09.24.zip` | 447 268 678 bytes | `1e7ce6a6f5e10d66571b6198852101ec0e9df31701846cef11df3654e86759b1` |

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
comercial), coqui-tts (MPL 2.0), PyTorch (BSD), torch-directml (MIT), Silero
VAD (MIT), FFmpeg via imageio-ffmpeg (GPL v3, processo separado), Python (PSF).

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
3. **Instalador offline de 2,8 GB**: sem hospedagem de release publicada, o
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
   de áudio em segundo plano e a ativação dentro da Game Bar. O 2.1.2 também
   inicia uma sessão silenciosa antes da síntese. O 2.1.3 passou a carregar o
   runtime .NET no próprio MSIX e valida a inicialização do runtime/XAML; a
   confirmação visual e auditiva em outra máquina continua sendo manual. No
   2.2.0, os testes automatizados cobrem o novo corte por pausas, referências
   acima de 30 minutos e cache permanente; a instalação em outra máquina
   limpa continua sendo uma validação manual pendente.
6. **Modelo XTTS v2**: 1,9 GB baixados de `huggingface.co/coqui/XTTS-v2` na
   instalação (ou fornecidos ao lado do instalador); a primeira fala após
   abrir o widget carrega o modelo (10–30 s em GPU).
7. **Idioma**: a síntese usa `pt` (português); outros idiomas do XTTS não
   estão expostos na interface.
