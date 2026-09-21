<#
.SYNOPSIS
Builds the SVoice XTTS runtime packs (embeddable Python + dependency packs).

.DESCRIPTION
Produces, under artifacts\runtime:

  svoice-python-<ver>.zip           embeddable CPython 3.12 + MSVC runtime DLLs
  svoice-pack-base-<ver>.zip        coqui-tts and every dependency except torch
  svoice-pack-torch-cpu-<ver>.zip   PyTorch CPU build
  svoice-pack-torch-cuda-<ver>.zip  PyTorch CUDA build (NVIDIA)
  svoice-pack-torch-directml-<ver>.zip  PyTorch 2.4.1 + torch-directml (experimental)
  manifest.json                     names, versions, sizes and SHA-256 of every archive

Requires Python 3.12 on the build machine (only for pip). End users never need
Python installed: the runtime ships its own interpreter.

.PARAMETER Packs
Which packs to build. Default: all.

.PARAMETER Clean
Delete the staging directory before building.
#>
[CmdletBinding()]
param(
    [string]$PythonPath = '',
    [ValidateSet('base', 'torch-cpu', 'torch-cuda', 'torch-directml')]
    [string[]]$Packs = @('base', 'torch-cpu', 'torch-cuda', 'torch-directml'),
    [switch]$Clean,
    [switch]$SkipArchive,
    [string]$RuntimeVersion = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$runtimeSource = $PSScriptRoot
$serviceRoot = Split-Path -Parent $runtimeSource
$repoRoot = Split-Path -Parent $serviceRoot
$toolsDir = Join-Path $repoRoot '.tools'
$buildDir = Join-Path $runtimeSource '.build'
$stageDir = Join-Path $buildDir 'stage'
$packsDir = Join-Path $buildDir 'packs'
$pythonDir = Join-Path $buildDir 'python'
$artifactsDir = Join-Path $repoRoot 'artifacts\runtime'

$embedVersion = '3.12.10'
$embedUrl = "https://www.python.org/ftp/python/$embedVersion/python-$embedVersion-embed-amd64.zip"
# SHA-256 of python-3.12.10-embed-amd64.zip. Empty = pin on first download (the
# value is printed and must then be recorded here); MD5 is cross-checked below.
$embedSha256 = '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'
$embedMd5 = 'fe8ef205f2e9c3ba44d0cf9954e1abd3'

if ([string]::IsNullOrWhiteSpace($RuntimeVersion)) {
    $RuntimeVersion = Get-Date -Format 'yyyy.MM.dd'
}

function Find-Python {
    param([string]$Requested)
    if (-not [string]::IsNullOrWhiteSpace($Requested)) { return $Requested }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
        'C:\Program Files\Python312\python.exe'
    )
    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $found) { throw 'Python 3.12 não encontrado para executar o pip.' }
    return $found
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-Pip {
    param([string[]]$Arguments)
    Write-Host "pip $($Arguments -join ' ')"
    & $python -m pip @Arguments --disable-pip-version-check --no-warn-script-location
    if ($LASTEXITCODE -ne 0) { throw "pip falhou: $($Arguments -join ' ')" }
}

function Remove-PipScripts {
    param([string]$Target)
    foreach ($name in @('bin', 'Scripts')) {
        $path = Join-Path $Target $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Get-ChildItem -LiteralPath $Target -Recurse -Directory -Filter '__pycache__' -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

function Write-PackMetadata {
    param([string]$Target, [string]$Name, [string]$Version, [string[]]$Backends, [hashtable]$Extra = @{})
    $count = (Get-ChildItem -LiteralPath $Target -Recurse -File | Measure-Object).Count
    $bytes = (Get-ChildItem -LiteralPath $Target -Recurse -File | Measure-Object Length -Sum).Sum
    $payload = [ordered]@{
        name = $Name
        version = $Version
        runtime_version = $RuntimeVersion
        python = $embedVersion
        backends = $Backends
        built_at = (Get-Date).ToUniversalTime().ToString('o')
        files = $count
        bytes = $bytes
    }
    foreach ($key in $Extra.Keys) { $payload[$key] = $Extra[$key] }
    $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Target '.svoice-pack.json') -Encoding utf8
    Write-Host ("Pack {0} {1}: {2} arquivos, {3:N0} MB" -f $Name, $Version, $count, ($bytes / 1MB))
}

function Get-DistVersion {
    param([string]$Target, [string]$Package)
    $info = Get-ChildItem -LiteralPath $Target -Directory -Filter "$Package-*.dist-info" | Select-Object -First 1
    if (-not $info) { return $null }
    $metadata = Get-Content -LiteralPath (Join-Path $info.FullName 'METADATA') -TotalCount 40
    $line = $metadata | Where-Object { $_ -match '^Version:\s*(.+)$' } | Select-Object -First 1
    if ($line -match '^Version:\s*(.+)$') { return $Matches[1].Trim() }
    return $null
}

function New-PackArchive {
    param([string]$Source, [string]$Archive)
    if (Test-Path -LiteralPath $Archive) { Remove-Item -LiteralPath $Archive -Force }
    Write-Host "Compactando $Archive"
    & tar.exe -a -cf $Archive -C $Source '.'
    if ($LASTEXITCODE -ne 0) { throw "tar falhou ao criar $Archive" }
    $size = (Get-Item -LiteralPath $Archive).Length
    if ($size -gt 1.95GB) {
        throw "O arquivo $Archive excede 1,95 GB ($([math]::Round($size / 1GB, 2)) GB); divida o pacote."
    }
}

$python = Find-Python $PythonPath
$pyVersion = & $python -c "import sys; print('%d.%d' % sys.version_info[:2])"
if ($pyVersion -ne '3.12') { throw "O pip precisa executar no Python 3.12 (encontrado $pyVersion)." }

if ($Clean -and (Test-Path -LiteralPath $buildDir)) {
    Remove-Item -LiteralPath $buildDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $buildDir, $packsDir, $toolsDir, $artifactsDir | Out-Null

# ----------------------------------------------------------------- embeddable python
$embedZip = Join-Path $toolsDir "python-$embedVersion-embed-amd64.zip"
if (-not (Test-Path -LiteralPath $embedZip)) {
    Write-Host "Baixando $embedUrl"
    Invoke-WebRequest -Uri $embedUrl -OutFile $embedZip -UseBasicParsing
}
$actualSha = Get-Sha256 $embedZip
$actualMd5 = (Get-FileHash -LiteralPath $embedZip -Algorithm MD5).Hash.ToLowerInvariant()
if ($embedSha256 -ne '' -and $actualSha -ne $embedSha256) {
    throw "SHA-256 do Python embutido não confere: esperado $embedSha256, obtido $actualSha"
}
if ($embedMd5 -ne '' -and $actualMd5 -ne $embedMd5) {
    throw "MD5 do Python embutido não confere com o publicado em python.org: esperado $embedMd5, obtido $actualMd5"
}
if ($embedSha256 -eq '') { Write-Warning "Python embutido baixado; registre no script: SHA-256 $actualSha / MD5 $actualMd5" }
if (Test-Path -LiteralPath $pythonDir) { Remove-Item -LiteralPath $pythonDir -Recurse -Force }
New-Item -ItemType Directory -Path $pythonDir | Out-Null
Expand-Archive -LiteralPath $embedZip -DestinationPath $pythonDir -Force
$pth = Get-ChildItem -LiteralPath $pythonDir -Filter 'python3*._pth' | Select-Object -First 1
@(
    'python312.zip'
    '.'
    'import site'
) | Set-Content -LiteralPath $pth.FullName -Encoding ascii

# MSVC runtime (app-local deployment of the VC++ redistributable DLLs).
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = if (Test-Path -LiteralPath $vswhere) { & $vswhere -latest -products * -property installationPath } else { '' }
$crtDirs = @()
if ($vsPath) {
    $crtDirs = Get-ChildItem -LiteralPath (Join-Path $vsPath 'VC\Redist\MSVC') -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\Microsoft\.VC\d+\.(CRT|OpenMP)$' -and $_.FullName -notmatch '\\onecore\\' }
}
if (-not $crtDirs) { throw 'Redistribuível do MSVC (Microsoft.VC*.CRT) não encontrado no Visual Studio.' }
foreach ($dir in $crtDirs) {
    Get-ChildItem -LiteralPath $dir.FullName -Filter '*.dll' | Copy-Item -Destination $pythonDir -Force
}
Write-PackMetadata -Target $pythonDir -Name 'python' -Version $embedVersion -Backends @() -Extra @{ source = $embedUrl; sha256 = $actualSha }

# ------------------------------------------------------------------------ base + cpu
$torchDirs = @('torch', 'torchaudio', 'torchgen', 'functorch', 'torch-*.dist-info', 'torchaudio-*.dist-info', 'torchcodec', 'torchcodec-*.dist-info')
if ($Packs -contains 'base' -or $Packs -contains 'torch-cpu') {
    if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
    Invoke-Pip @('install', '--target', $stageDir, '--extra-index-url', 'https://download.pytorch.org/whl/cpu',
        '-r', (Join-Path $runtimeSource 'requirements-base.txt'))
    Remove-PipScripts $stageDir

    $cpuPack = Join-Path $packsDir 'torch-cpu'
    if (Test-Path -LiteralPath $cpuPack) { Remove-Item -LiteralPath $cpuPack -Recurse -Force }
    New-Item -ItemType Directory -Path $cpuPack | Out-Null
    foreach ($pattern in $torchDirs) {
        Get-ChildItem -LiteralPath $stageDir -Filter $pattern -ErrorAction SilentlyContinue |
            Move-Item -Destination $cpuPack -Force
    }
    $torchVersion = Get-DistVersion $cpuPack 'torch'
    Write-PackMetadata -Target $cpuPack -Name 'torch-cpu' -Version $torchVersion -Backends @('cpu')

    $basePack = Join-Path $packsDir 'base'
    if (Test-Path -LiteralPath $basePack) { Remove-Item -LiteralPath $basePack -Recurse -Force }
    Move-Item -LiteralPath $stageDir -Destination $basePack
    $ttsVersion = Get-DistVersion $basePack 'coqui_tts'
    Write-PackMetadata -Target $basePack -Name 'base' -Version "$RuntimeVersion+tts$ttsVersion" -Backends @() -Extra @{ coqui_tts = $ttsVersion }
}

# ----------------------------------------------------------------------------- cuda
if ($Packs -contains 'torch-cuda') {
    $cudaPack = Join-Path $packsDir 'torch-cuda'
    if (Test-Path -LiteralPath $cudaPack) { Remove-Item -LiteralPath $cudaPack -Recurse -Force }
    Invoke-Pip @('install', '--target', $cudaPack, '--no-deps', '-r', (Join-Path $runtimeSource 'requirements-torch-cuda.txt'))
    Remove-PipScripts $cudaPack
    Write-PackMetadata -Target $cudaPack -Name 'torch-cuda' -Version (Get-DistVersion $cudaPack 'torch') -Backends @('cuda', 'cpu') -Extra @{
        requires = @{ vendor = 'nvidia'; nvidia_driver_min = '580.00'; compute_capability_min = '7.5'; cuda = '13.0' }
    }
}

# ------------------------------------------------------------------------- directml
if ($Packs -contains 'torch-directml') {
    $dmlPack = Join-Path $packsDir 'torch-directml'
    if (Test-Path -LiteralPath $dmlPack) { Remove-Item -LiteralPath $dmlPack -Recurse -Force }
    Invoke-Pip @('install', '--target', $dmlPack, '--no-deps', '-r', (Join-Path $runtimeSource 'requirements-torch-directml.txt'))
    Remove-PipScripts $dmlPack
    $dmlVersion = "$(Get-DistVersion $dmlPack 'torch')+dml$(Get-DistVersion $dmlPack 'torch_directml')"
    Write-PackMetadata -Target $dmlPack -Name 'torch-directml' -Version $dmlVersion -Backends @('directml', 'cpu') -Extra @{
        experimental = $true
        requires = @{ vendor = 'amd|intel|nvidia'; directx = '12' }
    }
}

# --------------------------------------------------------------------------- manifest
if ($SkipArchive) { Write-Host 'Pacotes preparados em' $packsDir; return }

$manifest = [ordered]@{
    manifest_version = 1
    runtime_version = $RuntimeVersion
    built_at = (Get-Date).ToUniversalTime().ToString('o')
    python = $null
    packs = [ordered]@{}
    download_base_url = 'https://github.com/maximosdrr/SVoice/releases/download/runtime-' + $RuntimeVersion + '/'
}

$pythonArchive = Join-Path $artifactsDir "svoice-python-$embedVersion.zip"
New-PackArchive -Source $pythonDir -Archive $pythonArchive
$manifest.python = [ordered]@{
    version = $embedVersion
    archive = (Split-Path -Leaf $pythonArchive)
    sha256 = (Get-Sha256 $pythonArchive)
    size = (Get-Item -LiteralPath $pythonArchive).Length
}

foreach ($packDir in Get-ChildItem -LiteralPath $packsDir -Directory) {
    $meta = Get-Content -LiteralPath (Join-Path $packDir.FullName '.svoice-pack.json') -Raw | ConvertFrom-Json
    $archive = Join-Path $artifactsDir ("svoice-pack-{0}-{1}.zip" -f $meta.name, $RuntimeVersion)
    New-PackArchive -Source $packDir.FullName -Archive $archive
    $entry = [ordered]@{
        version = $meta.version
        archive = (Split-Path -Leaf $archive)
        sha256 = (Get-Sha256 $archive)
        size = (Get-Item -LiteralPath $archive).Length
        unpacked_bytes = $meta.bytes
        backends = @($meta.backends)
    }
    if ($meta.PSObject.Properties['experimental']) { $entry.experimental = [bool]$meta.experimental }
    if ($meta.PSObject.Properties['requires']) { $entry.requires = $meta.requires }
    $manifest.packs[$meta.name] = $entry
}

$manifestPath = Join-Path $artifactsDir 'manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $runtimeSource 'manifest.json') -Force
Write-Host "Manifesto gravado em $manifestPath"
Get-ChildItem -LiteralPath $artifactsDir | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB) } } | Format-Table -AutoSize
