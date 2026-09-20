# Mecanismo XTTS do SVoice

Este processo é iniciado e encerrado automaticamente pelo SVoice. O usuário
final não precisa instalar Python nem abrir um segundo programa.

O executável é gerado em modo `onedir` para evitar a extração de vários
gigabytes a cada inicialização:

```powershell
.\python_service\build-service.ps1
```

O modelo XTTSv2 não é colocado dentro do instalador. Após o usuário aceitar a
licença na interface, o primeiro uso baixa o modelo para
`%LOCALAPPDATA%\SVoice\XTTS\models`.

O serviço escuta somente em `127.0.0.1`, em uma porta aleatória, e cada
execução recebe do SVoice um token novo de autenticação.
