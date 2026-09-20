param(
  [string]$IsccPath = '',
  [string]$PythonPath = '',
  [switch]$SkipXtssBuild
)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$nugetDirectory = Join-Path $projectDirectory '.tools'
$releaseDirectory = Join-Path $projectDirectory 'build\windows\x64\runner\Release'
$vbCableDirectory = Join-Path $PSScriptRoot 'vendor\VBCABLE'
$xttsBuildScript = Join-Path $projectDirectory 'python_service\build-service.ps1'
$xttsExecutable = Join-Path $projectDirectory 'python_service\dist\svoice_xtts_service\svoice_xtts_service.exe'

if (-not (Test-Path -LiteralPath (Join-Path $nugetDirectory 'nuget.exe'))) {
  throw 'nuget.exe não encontrado em .tools. Baixe-o antes de compilar.'
}

if (-not (Test-Path -LiteralPath (Join-Path $vbCableDirectory 'VBCABLE_Setup_x64.exe'))) {
  throw 'O pacote oficial do VB-CABLE não foi encontrado em installer\vendor\VBCABLE.'
}

$signature = Get-AuthenticodeSignature (Join-Path $vbCableDirectory 'VBCABLE_Setup_x64.exe')
if ($signature.Status -ne 'Valid') {
  throw 'A assinatura do instalador oficial do VB-CABLE não é válida.'
}

$env:PATH = "$nugetDirectory;$env:PATH"

if (-not $SkipXtssBuild) {
  & $xttsBuildScript -PythonPath $PythonPath
  if ($LASTEXITCODE -ne 0) {
    throw 'A compilação do mecanismo XTTS falhou.'
  }
}

if (-not (Test-Path -LiteralPath $xttsExecutable)) {
  throw 'O mecanismo XTTS não foi encontrado. Compile-o antes de gerar o instalador.'
}

Push-Location $projectDirectory
try {
  flutter pub get
  flutter build windows --release
}
finally {
  Pop-Location
}

if (-not (Test-Path -LiteralPath (Join-Path $releaseDirectory 'SVoice.exe'))) {
  throw 'A compilação do SVoice não produziu SVoice.exe.'
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
  $isccCandidates = @(
    (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
  )
  $IsccPath = $isccCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($IsccPath) -or
    -not (Test-Path -LiteralPath $IsccPath)) {
  throw 'Compilador do Inno Setup 7 não encontrado.'
}

& $IsccPath (Join-Path $PSScriptRoot 'SVoice.iss')
if ($LASTEXITCODE -ne 0) {
  throw "O Inno Setup encerrou com o código $LASTEXITCODE."
}
