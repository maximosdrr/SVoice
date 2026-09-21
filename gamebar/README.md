# SVoice para Xbox Game Bar

Widget UWP fixável e transparente para usar o SVoice dentro dos jogos. Ele usa
o TTS do Windows ou os perfis clonados pelo XTTSv2 e, quando encontra
`CABLE Input`, envia a voz para o VB-CABLE. O modo **ECO** reproduz a mesma
fala na saída padrão do Windows ao mesmo tempo, para que você ouça exatamente
quando a mensagem termina.

## Uso

1. Instale primeiro o SVoice completo, que contém o mecanismo XTTS.
2. Instale o pacote MSIX ou registre a compilação de desenvolvimento.
3. Pressione `Win + G`.
4. Abra o menu de widgets e escolha **SVoice**.
5. Escolha uma voz na lista ou use **CLONAR** para criar um perfil XTTS a
   partir de um ou mais áudios de referência.
6. Fixe o widget pelo alfinete da barra superior.
7. Digite uma frase e pressione `Enter`.
8. Ative **ECO** para monitorar a fala pelos seus fones ou alto-falantes.

Para interagir com o campo durante o jogo, abra a Game Bar com `Win + G`. Ao
fechá-la, o painel fixado permanece visível sem capturar os controles do jogo.

## Compilar pelo terminal

O ambiente precisa do Visual Studio 2026 (ou posterior) com as ferramentas UWP,
do Windows SDK 10.0.26100 e do .NET SDK 10.

```powershell
.\gamebar\build-widget.ps1 -Configuration Debug
```

Para registrar a compilação local no Windows:

```powershell
.\gamebar\install-widget-dev.ps1
```

O script confirma a presença da extensão `SVoiceWidget` no catálogo usado pela
Game Bar. O bridge XTTS já é incluído no MSIX; ele inicia o mecanismo instalado
pelo SVoice quando o widget consulta, clona ou reproduz uma voz. Depois disso,
abra `Win + G` e selecione **SVoice** no menu de widgets.

Para gerar o pacote Release assinado e o instalador local:

```powershell
.\gamebar\build-widget-package.ps1
```

Os arquivos prontos ficam em `artifacts\gamebar`. Execute
`Install-SVoice-GameBar.ps1` nessa pasta e aceite a elevação do Windows. O
script confia somente no certificado local do SVoice, instala o MSIX e valida
a extensão automaticamente.

## Compilar pelo Visual Studio

Abra `SVoice.GameBar.csproj`, selecione `x64` e compile. O pacote gerado fica em
`SVoice.GameBar\AppPackages`.
