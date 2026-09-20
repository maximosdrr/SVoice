# Mecanismo XTTS do SVoice

Este processo é iniciado e encerrado automaticamente pelo SVoice. O usuário
final não precisa instalar Python nem abrir um segundo programa.

O executável é gerado em modo `onedir` para evitar a extração de vários
gigabytes a cada inicialização:

```powershell
.\python_service\build-service.ps1
```

O modelo XTTSv2 não é colocado dentro do instalador. O primeiro uso baixa o
modelo para `%LOCALAPPDATA%\SVoice\XTTS\models`.

Ao importar uma voz, o serviço converte um ou vários arquivos para WAV mono,
divide o material em trechos de 20 segundos e usa no máximo os primeiros 30
minutos. O processamento intermediário ocorre na pasta temporária do serviço,
que é limpa ao final da importação.

O serviço escuta somente em `127.0.0.1`, em uma porta aleatória, e cada
execução recebe do SVoice um token novo de autenticação.
