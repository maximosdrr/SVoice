# Solução de problemas

Logs úteis:

| Componente | Arquivo |
| --- | --- |
| Instalador (Inno Setup) | `%TEMP%\Setup Log <data>.txt` |
| Assistente `SVoice.Setup` | `%LOCALAPPDATA%\SVoice\Logs\setup.log` e `install-diagnostics.json` |
| Serviço XTTS | `%LOCALAPPDATA%\SVoice\Logs\xtts-service.log` (rotação 5 × 2 MB) |
| Bridge do widget | `%LOCALAPPDATA%\SVoice\Logs\gamebar-bridge.log` |
| Widget | `%LOCALAPPDATA%\Packages\SVoice.GameBar_61qvngw278t1j\LocalState\gamebar.log` |

*Iniciar › SVoice › Diagnóstico do SVoice* imprime um relatório completo.

## O widget não aparece no menu da Game Bar

1. Confirme que a Xbox Game Bar está instalada (`Win + G`). Se não abrir,
   instale-a pela Microsoft Store.
2. Execute **Reparar SVoice**: ele reinstala o pacote `SVoice.GameBar` e
   confirma a extensão `SVoiceWidget` no catálogo.
3. Se a instalação usou o certificado de desenvolvimento, ele precisa estar em
   *Pessoas confiáveis* do computador (o instalador faz isso; o reparo pede
   elevação para repetir).

## `XTTS INDISPONÍVEL` no widget

- Clique em **Ajustes › Diagnóstico › RECONECTAR**. O bridge reinicia o serviço.
- Verifique `gamebar-bridge.log`: "O mecanismo XTTS do SVoice não está
  instalado" indica runtime ausente em `C:\Program Files\SVoice\runtime` —
  execute o instalador novamente.
- `xtts-service.log` mostra erros de importação do PyTorch (por exemplo, DLLs
  ausentes). Reinstale escolhendo **Somente CPU** para isolar problemas de GPU.

## `MODELO AUSENTE`

Abra **Ajustes › Diagnóstico › MODELO** (download de 1,9 GB com verificação
SHA-256; pode ser retomado). Sem internet, copie a pasta `xtts_v2` oficial para
`%ProgramData%\SVoice\models\tts\tts_models--multilingual--multi-dataset--xtts_v2`
e repita a verificação. Arquivos corrompidos são detectados e baixados de novo.

## A GPU não é usada

- **Ajustes › Diagnóstico** mostra o backend ativo, o recomendado e o *motivo do
  fallback*. Use **TESTAR BACKEND** para repetir a validação.
- NVIDIA: driver anterior ao 580 ou placa anterior a Turing → CPU. Atualize o
  driver e use **Reparar SVoice**; para trocar o pack instalado (`torch-cpu` →
  `torch-cuda`), execute o instalador e escolha **NVIDIA CUDA**.
- AMD/Intel: o DirectML é experimental; falhas de operação registram o motivo e
  mantêm a CPU. Não há aceleração ROCm nesta versão.
- Memória de vídeo insuficiente (< 3 GB livres) gera *out of memory* → CPU.

## Não sai som no Discord

1. Em **Ajustes › Saída de áudio** do widget, selecione **CABLE Input**.
2. No Discord, o microfone deve ser **CABLE Output (VB-Audio Virtual Cable)**.
3. Se `CABLE Input`/`CABLE Output` não existem: reinicie o Windows (driver
   recém-instalado) ou execute **Reparar SVoice**, que reinstala o VB-CABLE.
4. Ative **ECO** para confirmar que a síntese está sendo reproduzida.

## A síntese demora muito

Em CPU cada frase leva alguns segundos por sentença. Frases curtas respondem
mais rápido; `Esc` cancela entre sentenças. Em GPU a primeira fala após abrir o
widget carrega o modelo (10–30 s); por padrão, o serviço permanece ativo por 15 minutos
sem uso. Para evitar um novo carregamento, ative **Ajustes › Manter XTTS
carregado**. O botão **ENCERRAR XTTS** libera a RAM/VRAM; use o mesmo botão ou
envie uma nova fala clonada para iniciá-lo novamente.

## Perfil marcado com `(!)`

O áudio de referência processado não foi encontrado (pasta
`%LOCALAPPDATA%\SVoice\XTTS\voices\<id>`). Exclua o perfil em **Vozes** e
crie-o novamente com o áudio original. O registro `profiles.json` é migrado
com backup em `backups\profiles-<data>.json`.

Se `profiles.json` estiver vazio, mas as pastas de voz ainda existirem, o
serviço procura automaticamente o backup mais recente que corresponda a esses
áudios e restaura somente os perfis órfãos. Um registro ilegível nunca é
sobrescrito silenciosamente; o diagnóstico informa `profile_registry_corrupted`
quando não existe backup recuperável.

## Erro ao clonar: caminho sem acesso local

Arquivos abertos diretamente de nuvem ou de bibliotecas virtuais podem não ter
caminho local. Copie o áudio para *Músicas* ou *Documentos* e selecione-o
novamente.

## Caminhos com espaços ou caracteres especiais

O runtime é instalado em `C:\Program Files\SVoice` e os dados em
`%LOCALAPPDATA%\SVoice`; ambos funcionam com nomes de usuário acentuados e
espaços. Evite pastas de instalação com mais de 120 caracteres (o instalador
bloqueia caminhos longos, pois o runtime contém caminhos internos extensos).

## Reinstalar do zero

1. Desinstale o **SVoice (Xbox Game Bar)** respondendo *Sim* às perguntas de
   exclusão de perfis e modelo (ou *Não* para mantê-los).
2. Opcionalmente remova o VB-CABLE com `VBCABLE_Setup_x64.exe -u`.
3. Execute o instalador novamente.
