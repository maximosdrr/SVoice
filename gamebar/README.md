# SVoice para Xbox Game Bar

Protótipo UWP do overlay do SVoice. O widget é fixável, transparente e usa o
TTS do Windows diretamente. Quando encontra `CABLE Input`, envia a reprodução
para o VB-CABLE automaticamente.

## Uso

1. Instale o pacote de desenvolvimento.
2. Pressione `Win + G`.
3. Abra o menu de widgets e escolha **SVoice**.
4. Fixe o widget pelo alfinete da barra superior.
5. Digite uma frase e pressione `Enter`.

Para interagir com o campo durante o jogo, abra a Game Bar com `Win + G`. Ao
fechá-la, o painel fixado permanece visível sem capturar os controles do jogo.

## Compilar

Abra `SVoice.GameBar.csproj` no Visual Studio 2022 ou posterior, selecione
`x64` e compile. O ambiente precisa da carga de trabalho UWP e do Windows SDK
10.0.19041 ou posterior.
