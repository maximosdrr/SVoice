<#
.SYNOPSIS
Builds the standalone SVoice installer (Xbox Game Bar widget + XTTS service).

.DESCRIPTION
1. Builds and signs the widget MSIX (gamebar\build-widget-package.ps1).
2. Publishes the SVoice.Setup helper (self-contained single file).
3. Reuses the runtime packs in artifacts\runtime (or builds them with
   service\runtime\build-runtime.ps1 when missing).
4. Stages everything in artifacts\installer-staging and compiles
   installer\SVoice.iss with Inno Setup.
5. Writes SHA-256 checksums next to the installer.

No Flutter, Python or Visual Studio is required on the end user's machine.
#>
[CmdletBinding()]
param(
  [string]$IsccPath = '',
  [string]$Version = '2.1.1',
  [switch]$SkipWidgetBuild,
  [switch]$SkipRuntimeBuild
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $projectDirectory 'artifacts'
$staging = Join-Path $artifacts 'installer-staging'
$runtimeArtifacts = Join-Path $artifacts 'runtime'
$widgetArtifacts = Join-Path $artifacts 'gamebar'
$vbCableDirectory = Join-Path $PSScriptRoot 'vendor\VBCABLE'
$helperProject = Join-Path $PSScriptRoot 'SVoice.Setup\SVoice.Setup.csproj'
$helperOutput = Join-Path $artifacts 'setup-helper'

# --- VB-CABLE official package -------------------------------------------------
if (-not (Test-Path -LiteralPath (Join-Path $vbCableDirectory 'VBCABLE_Setup_x64.exe'))) {
  throw 'O pacote oficial do VB-CABLE não foi encontrado em installer\vendor\VBCABLE.'
}
$signature = Get-AuthenticodeSignature (Join-Path $vbCableDirectory 'VBCABLE_Setup_x64.exe')
if ($signature.Status -ne 'Valid') {
  throw "A assinatura do instalador oficial do VB-CABLE não é válida ($($signature.Status))."
}
Write-Host "VB-CABLE: assinatura válida ($($signature.SignerCertificate.Subject))"

# --- Widget ----------------------------------------------------------------------
if (-not $SkipWidgetBuild) {
  & (Join-Path $projectDirectory 'gamebar\build-widget-package.ps1')
}
$msix = Get-ChildItem -LiteralPath $widgetArtifacts -Filter 'SVoice.GameBar_*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$cer = Join-Path $widgetArtifacts 'SVoice.GameBar.cer'
if ($null -eq $msix -or -not (Test-Path -LiteralPath $cer)) {
  throw 'O MSIX assinado do widget não foi encontrado em artifacts\gamebar.'
}

# --- Helper ----------------------------------------------------------------------
if (Test-Path -LiteralPath $helperOutput) { Remove-Item -LiteralPath $helperOutput -Recurse -Force }
& dotnet publish $helperProject -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $helperOutput --nologo
if ($LASTEXITCODE -ne 0) { throw 'A publicação do SVoice.Setup falhou.' }
$helperExe = Join-Path $helperOutput 'SVoice.Setup.exe'
if (-not (Test-Path -LiteralPath $helperExe)) { throw 'SVoice.Setup.exe não foi produzido.' }

# --- Runtime packs ---------------------------------------------------------------
$manifestPath = Join-Path $runtimeArtifacts 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath) -and -not $SkipRuntimeBuild) {
  & (Join-Path $projectDirectory 'service\runtime\build-runtime.ps1')
}
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'artifacts\runtime\manifest.json não encontrado; execute service\runtime\build-runtime.ps1.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$runtimeFiles = @($manifest.python.archive)
foreach ($pack in $manifest.packs.PSObject.Properties) { $runtimeFiles += $pack.Value.archive }
foreach ($file in $runtimeFiles) {
  $path = Join-Path $runtimeArtifacts $file
  if (-not (Test-Path -LiteralPath $path)) { throw "Arquivo do runtime ausente: $path" }
}

# --- Staging ----------------------------------------------------------------------
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging, "$staging\widget", "$staging\service", "$staging\runtime", "$staging\vendor\VBCABLE", "$staging\licenses", "$staging\docs" | Out-Null
Copy-Item -LiteralPath $helperExe -Destination $staging
Copy-Item -LiteralPath $msix.FullName -Destination "$staging\widget"
Copy-Item -LiteralPath $cer -Destination "$staging\widget"
Copy-Item -LiteralPath (Join-Path $projectDirectory 'service\svoice_xtts_service.py') -Destination "$staging\service"
Copy-Item -Path (Join-Path $projectDirectory 'service\svoice_xtts') -Destination "$staging\service\svoice_xtts" -Recurse
Get-ChildItem -LiteralPath "$staging\service" -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force
Copy-Item -LiteralPath $manifestPath -Destination "$staging\runtime"
foreach ($file in $runtimeFiles) { Copy-Item -LiteralPath (Join-Path $runtimeArtifacts $file) -Destination "$staging\runtime" }
Copy-Item -Path (Join-Path $vbCableDirectory '*') -Destination "$staging\vendor\VBCABLE" -Recurse
Copy-Item -Path (Join-Path $PSScriptRoot 'licenses\*') -Destination "$staging\licenses"
foreach ($doc in @('instalacao-e-uso.md', 'backends.md', 'seguranca.md', 'solucao-de-problemas.md')) {
  $source = Join-Path $projectDirectory "docs\$doc"
  if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination "$staging\docs" }
}
Copy-Item -LiteralPath (Join-Path $projectDirectory 'README.md') -Destination "$staging\docs\README.md"

# --- Inno Setup --------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
  $isccCandidates = @(
    (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
  )
  $IsccPath = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath)) {
  throw 'Compilador do Inno Setup não encontrado.'
}

& $IsccPath "/DStagingDir=$staging" "/DMyAppVersion=$Version" (Join-Path $PSScriptRoot 'SVoice.iss')
if ($LASTEXITCODE -ne 0) { throw "O Inno Setup encerrou com o código $LASTEXITCODE." }

$installer = Join-Path $artifacts "SVoice-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'O instalador não foi produzido.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$checksums = @("$hash  $(Split-Path -Leaf $installer)")
foreach ($file in $runtimeFiles) {
  $checksums += "$((Get-FileHash -LiteralPath (Join-Path $runtimeArtifacts $file) -Algorithm SHA256).Hash.ToLowerInvariant())  runtime/$file"
}
$checksums += "$((Get-FileHash -LiteralPath $msix.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  widget/$($msix.Name)"
Set-Content -LiteralPath (Join-Path $artifacts "SVoice-Setup-$Version.sha256.txt") -Value $checksums -Encoding ascii
Write-Host "Instalador: $installer"
Write-Host "SHA-256: $hash"
Write-Host ("Tamanho: {0:N0} MB" -f ((Get-Item -LiteralPath $installer).Length / 1MB))
