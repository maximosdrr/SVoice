# Segurança e confiabilidade

| Requisito | Implementação |
| --- | --- |
| Comunicação local restrita ao usuário | Serviço escuta apenas em `127.0.0.1` com porta aleatória e token Bearer de 64 hex; o arquivo de descoberta `service.json` fica em `%LOCALAPPDATA%` e recebe ACL apenas do usuário (`icacls`). Widget ↔ bridge usa App Service do próprio pacote MSIX; o pipe alternativo tem DACL do usuário + SID do pacote e rótulo de integridade baixa. |
| Validação de mensagens | Bridge: comandos de lista fixa, `protocol` obrigatório (v2), campos tipados. Serviço: JSON ≤ 1 MB, texto ≤ 1000 caracteres, velocidade 0,5–2,0, modo de processamento em lista fechada, nomes de perfil saneados (80 caracteres, sem controle). |
| Limites para arquivos | Até 12 áudios de referência, 512 MB cada, extensões `.wav .mp3 .m4a .flac .ogg`, conteúdo confirmado por decodificação FFmpeg de 0,5 s. Não há limite de duração total; há verificação prévia de espaço livre. |
| Rejeição de caminhos inseguros | Caminhos de referência precisam ser absolutos, existentes e regulares; `GET /audio` só serve WAV dentro de `temp`; exclusão de perfil só remove subpastas diretas de `voices`; o bridge confere que o áudio devolvido está em `temp\*.wav`. |
| Timeouts | HTTP: 30 s padrão, 45 min para síntese e até 24 h para uma importação longa; o timeout do FFmpeg cresce conforme a duração do áudio e não funciona como truncamento; análise inicial tem 2 min; serviço encerra após 15 min sem requisições. |
| Cancelamento | `POST /jobs/cancel` cancela entre sentenças, etapas de clonagem, download do modelo e testes; `Esc` no widget. |
| Exclusão segura de temporários | Utterances apagadas pelo bridge após leitura; `import_*` e WAVs limpos ao iniciar o serviço; arquivo de transferência em `TempState` apagado pelo widget. |
| Logs sem dados pessoais | Nenhum texto sintetizado, nome de arquivo do usuário ou token é registrado; logs guardam contagens, ids de perfil, estados e erros. Rotação 5 × 2 MB. |
| Sem exposição na rede | Nenhum socket em `0.0.0.0`; nenhuma porta de entrada além do loopback. |
| Sem execução arbitrária | O bridge não aceita caminhos de executáveis nem argumentos livres; o serviço só executa o FFmpeg embutido com argumentos fixos. |
| Hash de downloads | Modelo XTTS v2 (5 arquivos) e packs de runtime verificados por SHA-256 pinado; downloads retomáveis com `.part`, descartados em caso de divergência. |
| Falta de espaço | `ensure_model`, `install-runtime` e a importação de referências verificam espaço livre antes de escrever; o instalador exige 12 GB. |
| Cache permanente | `conditioning.pt` é gravado por substituição atômica e reutilizado sem TTL ou invalidação por versão. Só a exclusão explícita do perfil remove o cache; corrupção é reportada sem apagar o arquivo silenciosamente. |
| Modelo incompleto | `check_model` compara tamanho e SHA-256 (cache de verificação por fingerprint); arquivos ausentes/corrompidos são baixados novamente. |
| Atualização/desinstalação sem perda | MSIX atualizado no lugar (`ForceUpdateFromAnyVersion`, dados do app preservados); dados do usuário ficam fora de `Program Files`; packs só são substituídos quando o hash muda. A desinstalação silenciosa sempre preserva perfis e modelo; exclusão exige confirmação interativa. Migração do registro cria backup. Leituras transitórias de `profiles.json` são repetidas e nunca viram um registro vazio; quando há áudios órfãos, um backup válido correspondente é recuperado automaticamente. |
| Instância única e recuperação | Mutex `Local\SVoice.XttsService`; se widget e instalador iniciarem juntos, a segunda instância aguarda a publicação atômica de `service.json` e devolve o endpoint autenticado para adoção. O bridge também aguarda e adota a instância existente, relança em caso de queda; o widget oferece **RECONECTAR**. |

Limite de confiança: qualquer processo executado pela mesma conta do usuário
pode ler `service.json` e usar o serviço — o mesmo limite do próprio perfil do
usuário. O serviço nunca é exposto a outros usuários ou à rede.
