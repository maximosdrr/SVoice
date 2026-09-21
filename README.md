# SVoice

Overlay desktop para Windows que transforma texto digitado em voz. A interface
fica sempre no topo, pode ser recolhida e tem transparência ajustável.

## Recursos

- texto para voz usando as vozes instaladas no Windows;
- clonagem local de voz com XTTSv2 a partir de um ou vários áudios de referência;
- processamento automático por GPU, com fallback para CPU, ou seleção manual
  de GPU/CPU;
- seleção de voz, volume, velocidade e tom;
- modo Eco para ouvir na saída padrão do Windows a mesma fala enviada ao
  microfone virtual;
- histórico curto com repetição por clique;
- `Enter` para falar e `Esc` para interromper;
- atalho global configurável para ocultar ou trazer a janela (inicialmente
  `Shift + Aspas`);
- modo compacto e opção de manter o overlay sempre no topo;
- widget fixável para Xbox Game Bar com clonagem XTTS, seleção de perfis e
  modo Eco independente;
- guia integrado para conectar a saída ao Discord;
- preferências salvas entre execuções.
- instalador completo com microfone virtual e roteamento automático do TTS.

## Clonagem de voz

O motor XTTS é iniciado e encerrado pelo próprio SVoice, sem uma segunda
janela e sem exigir que o usuário instale ou abra o Python. Em **Configurações
> Vozes clonadas**, selecione um ou vários áudios limpos de uma única pessoa.
O nome é opcional; se ficar vazio, o SVoice usa o nome do primeiro arquivo. O
SVoice aceita até 30 minutos no total, corta o material em trechos de 20
segundos e descarta automaticamente o conteúdo excedente.

O modo **Automático** usa uma GPU NVIDIA compatível quando disponível. Se a
GPU não estiver disponível ou não concluir a geração, o SVoice tenta novamente
pela CPU. Também é possível forçar **GPU** ou **CPU** nas configurações. A CPU
funciona sem placa de vídeo dedicada, mas a primeira carga e cada fala podem
demorar mais.

O modelo XTTSv2 não fica dentro do instalador: ele é baixado pelo próprio
SVoice na primeira geração. O download e os perfis ficam em
`%LOCALAPPDATA%\SVoice\XTTS`.
Use somente vozes para as quais você tenha autorização.

## Usar no Discord

O instalador completo inclui o pacote oficial do VB-CABLE, um driver puramente
virtual — nenhum cabo ou equipamento físico é necessário.

1. Execute o SVoice Setup como administrador e mantenha marcada a instalação
   do microfone virtual.
2. Reinicie o Windows quando o instalador solicitar.
3. O SVoice detectará e selecionará `CABLE Input` automaticamente.
4. No Discord, em **Configurações > Voz e vídeo**, selecione `CABLE Output`
   como dispositivo de entrada.
5. Se o começo ou o fim das frases for cortado, desative a supressão de ruído
   e ajuste manualmente a sensibilidade de entrada do Discord.

Nas configurações do SVoice, ative **Modo Eco** para ouvir simultaneamente na
saída padrão do Windows a fala que está sendo enviada ao `CABLE Input`. O Eco
só fica ativo enquanto o microfone virtual estiver selecionado, evitando
duplicação quando o aplicativo já estiver reproduzindo diretamente nos
alto-falantes ou fones.

O botão **Conectar ao Discord** dentro do app mostra essas instruções e abre o
Mixer de volume do Windows.

VB-CABLE é um donationware da VB-Audio Software. O pacote base oficial é
redistribuído sem modificações de acordo com as condições publicadas em
https://vb-audio.com/Services/licensing.htm. Contribuições ao projeto original
são bem-vindas em https://vb-audio.com/Cable/.

## Executar em desenvolvimento

Requisitos para o aplicativo: Flutter com suporte a desktop Windows, Visual
Studio com a carga de trabalho C++ e `nuget.exe` disponível no `PATH`. Para
gerar o serviço integrado de clonagem, use também Python 3.11 ou 3.12; esse
Python é necessário somente no computador de desenvolvimento.

```powershell
flutter pub get
flutter run -d windows
```

## Widget da Xbox Game Bar

O widget permite digitar, clonar uma voz com áudios de referência, selecionar
perfis XTTS e transmitir o TTS sem sair do jogo. Para gerar o pacote MSIX
assinado e seu instalador local:

```powershell
.\gamebar\build-widget-package.ps1
```

Os arquivos são gerados em `artifacts\gamebar`. Mais detalhes estão em
`gamebar\README.md`.

## Validar e compilar

```powershell
flutter analyze
flutter test
flutter build windows --release
```

Para compilar o mecanismo XTTS e gerar o instalador completo:

```powershell
.\python_service\build-service.ps1
.\installer\build-installer.ps1 -SkipXtssBuild
```

O pacote de produção é gerado em
`build/windows/x64/runner/Release`. Mantenha o executável, as DLLs e a pasta
`data` juntos ao distribuir o aplicativo.
