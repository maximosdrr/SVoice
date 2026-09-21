# SVoice

SVoice é um widget standalone para Xbox Game Bar que transforma texto em voz
clonada localmente com XTTS v2 e envia o áudio ao microfone virtual VB-CABLE.
A versão 2 não depende do aplicativo Flutter, de Python instalado pelo usuário
ou de ferramentas de desenvolvimento.

## Recursos

- clonagem local de voz a partir de WAV, MP3, M4A, FLAC ou OGG;
- síntese XTTS v2 em português diretamente pela Xbox Game Bar;
- histórico, renomeação e exclusão de perfis;
- envio automático para `CABLE Input`;
- modo Eco para ouvir a mesma fala na saída padrão;
- aceleração NVIDIA CUDA;
- AMD/Intel DirectML experimental, ativado somente após uma síntese de
  validação completa;
- fallback automático para CPU;
- cancelamento, progresso e diagnóstico do backend;
- instalador offline dos runtimes com o pacote oficial do VB-CABLE.

Use apenas vozes para as quais você tenha autorização. O modelo XTTS v2 usa a
Coqui Public Model License 1.0.0, destinada a uso não comercial.

## Instalação

Execute `SVoice-Setup-2.0.1.exe` como administrador, mantenha marcadas as
opções de VB-CABLE e modelo XTTS e deixe o backend em **Automático**. Se o
driver for instalado pela primeira vez, reinicie o Windows.

Depois:

1. Abra a Xbox Game Bar com `Win + G`.
2. Abra o menu de widgets e escolha **SVoice**.
3. No Discord, selecione **CABLE Output (VB-Audio Virtual Cable)** como
   microfone.
4. No widget, clone ou selecione uma voz, escreva a frase e pressione `Enter`.

Consulte [instalação e uso](docs/instalacao-e-uso.md),
[backends](docs/backends.md), [segurança](docs/seguranca.md) e
[solução de problemas](docs/solucao-de-problemas.md).

## Compilação

Requisitos de desenvolvimento:

- Windows 10/11 x64;
- .NET SDK 10;
- Visual Studio Build Tools com SDK UWP/Windows;
- Inno Setup 7;
- PowerShell 7;
- conexão durante a criação inicial dos packs de runtime.

Gerar runtimes, MSIX e instalador:

```powershell
.\service\runtime\build-runtime.ps1
.\gamebar\build-widget-package.ps1
.\installer\build-installer.ps1 -SkipRuntimeBuild -SkipWidgetBuild
```

Os artefatos são criados em `artifacts/`, diretório ignorado pelo Git.

## Testes

```powershell
dotnet build .\installer\SVoice.Setup\SVoice.Setup.csproj -c Release -p:Platform=x64
python -m pytest .\service\tests -q
```

O aceite de um backend de GPU exige também o teste integrado de carga do
modelo, síntese curta e longa, chamadas consecutivas, cancelamento e fallback.

## Código Flutter legado

O instalador e o widget 2.0 já não usam Flutter. O código Flutter permanece
temporariamente no histórico de trabalho apenas até o gate final de paridade,
instalação, atualização e desinstalação. Depois desse aceite ele será removido
do branch principal, permanecendo recuperável pela tag `svoice-pre-standalone`.

## VB-CABLE

VB-CABLE é software donationware da VB-Audio Software e é redistribuído sem
modificações. Origem e contribuições: <https://vb-cable.com> e
<https://vb-audio.com/Cable/>.
