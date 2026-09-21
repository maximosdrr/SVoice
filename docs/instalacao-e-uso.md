# SVoice para Xbox Game Bar — instalação e uso

O SVoice transforma texto em voz clonada (XTTS v2) diretamente no widget da
Xbox Game Bar e envia o áudio para um microfone virtual (VB-CABLE), para uso
no Discord ou em qualquer aplicativo. Não é necessário instalar Python,
Flutter, Visual Studio ou qualquer SDK: o instalador contém tudo.

## Requisitos

| Item | Mínimo |
| --- | --- |
| Windows | Windows 10 versão 2004 (build 19041) ou Windows 11, x64 |
| Xbox Game Bar | Instalada (vem com o Windows; disponível na Microsoft Store) |
| Espaço em disco | 12 GB livres (runtime até 3,5 GB + modelo 1,9 GB + temporários) |
| Internet | Necessária na primeira instalação para baixar o modelo XTTS v2 (1,9 GB), a menos que a pasta `xtts_v2` esteja ao lado do instalador |
| GPU (opcional) | NVIDIA Turing/RTX ou mais nova com driver 580+ (CUDA 13); AMD/Intel com DirectX 12 (DirectML, experimental). Sem GPU o SVoice usa a CPU |

## Instalação (primeira vez)

1. Execute `SVoice-Setup-<versão>.exe` e aceite a elevação (o driver VB-CABLE
   e o runtime são instalados em `C:\Program Files\SVoice`).
2. Leia o aviso do VB-CABLE (software da VB-Audio Software, donationware —
   https://vb-cable.com).
3. Mantenha marcadas as tarefas **Instalar o microfone virtual VB-CABLE** e
   **Baixar o modelo XTTS v2**.
4. Na página **Aceleração de hardware**, deixe **Automático**: o instalador
   detecta a GPU e instala apenas o runtime necessário (CUDA, DirectML ou CPU).
5. Aguarde as etapas: certificado e widget, VB-CABLE, runtime, modelo, teste de
   síntese e verificação final. O teste executa uma síntese completa na GPU
   escolhida; se falhar, a CPU é validada e passa a ser usada.
6. Se o instalador pedir para **reiniciar o Windows** (necessário quando o
   VB-CABLE foi instalado pela primeira vez), reinicie. Após o login, o SVoice
   verifica automaticamente se `CABLE Input`/`CABLE Output` ficaram ativos.

Uma versão anterior do SVoice (aplicativo de área de trabalho) é removida
automaticamente pelo instalador; perfis de voz e o modelo são preservados.

## Uso

1. Pressione `Win + G` para abrir a Xbox Game Bar.
2. No menu de widgets, escolha **SVoice** e fixe o widget pelo alfinete.
3. Clique em **CLONAR**, selecione um ou mais áudios (WAV, MP3, M4A, FLAC ou
   OGG) de uma única pessoa — de 10 segundos a 30 minutos no total — e dê um
   nome. Use apenas vozes com autorização do titular.
4. Selecione a voz clonada em **VOZ**, digite a frase e pressione `Enter`.
   `Esc` interrompe a geração ou a reprodução.
5. O áudio é enviado para **CABLE Input**. No Discord, em *Configurações ›
   Voz e vídeo*, escolha **CABLE Output (VB-Audio Virtual Cable)** como
   microfone. Desative a supressão de ruído do Discord se o início das frases
   for cortado.
6. Ative **ECO** para ouvir a mesma fala nos seus fones ou alto-falantes.

Botões do cabeçalho:

- **Histórico** — últimas 8 frases; clique para repetir.
- **Vozes** — renomear ou excluir perfis clonados.
- **Ajustes** — processamento (Automático, NVIDIA CUDA, AMD DirectML, CPU),
  saída de áudio, velocidade e volume.
- **Diagnóstico** — GPU detectada, backend ativo, motivo de fallback, tempo da
  síntese de teste, estado do modelo; botões **TESTAR BACKEND**, **RECONECTAR**
  e **VERIFICAR MODELO**.

A **Voz do Windows** aparece como opção explícita; ela nunca substitui a voz
clonada silenciosamente. Se o XTTS não estiver pronto, o widget mostra o estado
real (`MODELO AUSENTE`, `XTTS INDISPONÍVEL`) e a ação sugerida.

## Onde ficam os dados

| Conteúdo | Local |
| --- | --- |
| Perfis clonados, configuração, logs do serviço | `%LOCALAPPDATA%\SVoice\XTTS` e `%LOCALAPPDATA%\SVoice\Logs` |
| Modelo XTTS v2 | `%ProgramData%\SVoice\models` (compartilhado) ou `%LOCALAPPDATA%\SVoice\XTTS\models` |
| Runtime (Python embutido, PyTorch), serviço, VB-CABLE, licenças | `C:\Program Files\SVoice` |
| Preferências e histórico do widget | Dados do aplicativo do pacote `SVoice.GameBar` |

Atualizações e reparos nunca apagam perfis nem o modelo.

## Atualização

Execute o novo `SVoice-Setup-<versão>.exe`. O widget é atualizado no lugar
(preferências preservadas), o runtime só é reinstalado quando a versão dos
packs muda e o modelo é apenas verificado.

## Reparo

*Iniciar › SVoice › Reparar SVoice* reinstala o widget se ele sumiu do
catálogo da Game Bar, repara o VB-CABLE, verifica o modelo e executa a
síntese de teste. Para restaurar arquivos do runtime, execute o instalador
novamente.

## Desinstalação

Em *Aplicativos instalados*, remova **SVoice (Xbox Game Bar)**. O
desinstalador pergunta separadamente se deseja excluir os **perfis de voz** e o
**modelo XTTS**; responda *Não* para preservá-los. O VB-CABLE é um driver
compartilhado e permanece instalado; para removê-lo use
`C:\Program Files\SVoice\vendor\VBCABLE\VBCABLE_Setup_x64.exe -u` antes de
desinstalar o SVoice, ou a opção correspondente em *Aplicativos instalados*.

## Instalação sem internet

Coloque a pasta `xtts_v2` (com `model.pth`, `config.json`, `vocab.json`,
`speakers_xtts.pth` e `hash.md5` do repositório oficial `coqui/XTTS-v2`) ao
lado do instalador. Os arquivos são copiados e verificados por SHA-256.

## Licenças e atribuições

- **VB-CABLE** © VB-Audio Software (Vincent Burel) — donationware.
  Origem: https://vb-cable.com. Contribuições: https://vb-audio.com/Cable/.
  Redistribuído sem modificações conforme https://vb-audio.com/Services/licensing.htm.
- **XTTS v2** — Coqui Public Model License 1.0.0 (uso não comercial);
  **coqui-tts** — MPL 2.0. **PyTorch** — BSD. **torch-directml** — MIT.
- **FFmpeg** (imageio-ffmpeg) — GPL v3; usado como processo separado para
  converter os áudios de referência.
- **Python** — PSF License.
