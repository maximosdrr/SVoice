# SVoice

SVoice é um widget standalone para Xbox Game Bar que transforma texto em voz
clonada localmente com XTTS v2 e envia o áudio ao microfone virtual VB-CABLE.
A versão 2 não depende do aplicativo Flutter, de Python instalado pelo usuário
ou de ferramentas de desenvolvimento.

## Recursos

- clonagem local de voz a partir de WAV, MP3, M4A, FLAC ou OGG;
- síntese XTTS v2 em português diretamente pela Xbox Game Bar;
- chat com balões das frases ditas, modo compacto (só a caixa de texto),
  aba de vozes com clonagem, renomeação e exclusão de perfis;
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

Execute `SVoice-Setup-2.1.3.exe` como administrador, mantenha marcadas as
opções de VB-CABLE e modelo XTTS e deixe o backend em **Automático**. Se o
driver for instalado pela primeira vez, reinicie o Windows.

Depois:

1. Abra a Xbox Game Bar com `Win + G`.
2. Abra o menu de widgets e escolha **SVoice**.
3. No Discord, selecione **CABLE Output (VB-Audio Virtual Cable)** como
   microfone.
4. No widget, escreva a frase e pressione `Enter`; o chip **VOZ** abre a aba
   de vozes para clonar ou trocar a voz.

Consulte [entrega e hashes](docs/entrega.md), [instalação e uso](docs/instalacao-e-uso.md),
[backends](docs/backends.md), [segurança](docs/seguranca.md),
[solução de problemas](docs/solucao-de-problemas.md), [testes executados](docs/testes.md) e o [changelog](CHANGELOG.md).

## Compilação

Requisitos de desenvolvimento:

- Windows 10/11 x64;
- .NET SDK 10;
- Visual Studio 2026 (ou Build Tools) com ferramentas UWP e Windows SDK 10.0.26100;
- Python 3.12 (apenas para o `pip` que monta os packs de runtime);
- Inno Setup 7;
- Windows PowerShell 5.1 ou PowerShell 7;
- conexão durante a criação inicial dos packs de runtime (download de wheels
  do PyTorch e do Python embutido).

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
.\gamebar\build-widget.ps1 -Configuration Debug
& "C:\Program Files\SVoice\runtime\python\python.exe" .\service\tools\run_tests.py
```

`run_tests.py` ativa os packs do runtime instalado (ou de `service\runtime`)
para que os testes rodem sem um ambiente virtual. O teste ponta a ponta com o
modelo real fica em `service\tools\service_client.py` (`e2e`).

O aceite de um backend de GPU exige também o teste integrado de carga do
modelo, síntese curta e longa, chamadas consecutivas, cancelamento e fallback.

## Histórico da versão desktop

O aplicativo Flutter legado foi removido depois da aprovação dos gates de
paridade, instalação, atualização e desinstalação. Ele continua recuperável na
tag Git `svoice-pre-standalone`; a versão atual é exclusivamente Xbox Game Bar
+ serviço XTTS standalone.

## VB-CABLE

VB-CABLE é software donationware da VB-Audio Software e é redistribuído sem
modificações. Origem e contribuições: <https://vb-cable.com> e
<https://vb-audio.com/Cable/>.
