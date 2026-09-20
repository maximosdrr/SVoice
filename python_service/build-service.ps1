param(
  [string]$PythonPath = '',
  [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$serviceDirectory = $PSScriptRoot
$venvDirectory = Join-Path $serviceDirectory '.build-venv'
$distDirectory = Join-Path $serviceDirectory 'dist'
$buildDirectory = Join-Path $serviceDirectory 'build'
$specPath = Join-Path $serviceDirectory 'svoice_xtts_service.spec'

if ([string]::IsNullOrWhiteSpace($PythonPath)) {
  $candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\python.exe'),
    'C:\Program Files\Python312\python.exe',
    'C:\Program Files\Python311\python.exe'
  )
  $PythonPath = $candidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($PythonPath) -or
    -not (Test-Path -LiteralPath $PythonPath)) {
  throw 'Python 3.11 ou 3.12 não encontrado para compilar o mecanismo XTTS.'
}

if ($Clean) {
  foreach ($target in @($venvDirectory, $distDirectory, $buildDirectory, $specPath)) {
    if (Test-Path -LiteralPath $target) {
      Remove-Item -LiteralPath $target -Recurse -Force
    }
  }
}

if (-not (Test-Path -LiteralPath (Join-Path $venvDirectory 'Scripts\python.exe'))) {
  & $PythonPath -m venv $venvDirectory
  if ($LASTEXITCODE -ne 0) {
    throw 'Não foi possível criar o ambiente de compilação do XTTS.'
  }
}

$venvPython = Join-Path $venvDirectory 'Scripts\python.exe'
$uv = Join-Path $venvDirectory 'Scripts\uv.exe'

& $venvPython -m pip install --disable-pip-version-check --upgrade pip uv
if ($LASTEXITCODE -ne 0) {
  throw 'Não foi possível instalar o gerenciador de dependências do XTTS.'
}

& $uv pip install --python $venvPython --torch-backend=auto -r (Join-Path $serviceDirectory 'requirements.txt')
if ($LASTEXITCODE -ne 0) {
  throw 'Não foi possível instalar as dependências do XTTS.'
}

Push-Location $serviceDirectory
try {
  & $venvPython -m PyInstaller `
    --noconfirm `
    --clean `
    --onedir `
    --noconsole `
    --disable-windowed-traceback `
    --name svoice_xtts_service `
    --distpath $distDirectory `
    --workpath $buildDirectory `
    --collect-all TTS `
    --collect-all trainer `
    --collect-all coqpit `
    --collect-all imageio_ffmpeg `
    --collect-all ko_speech_tools `
    --collect-data transformers `
    --copy-metadata torchcodec `
    --hidden-import TTS.tts.configs.xtts_config `
    --hidden-import TTS.tts.models.xtts `
    --hidden-import torchaudio `
    service.py
  if ($LASTEXITCODE -ne 0) {
    throw 'A compilação do mecanismo XTTS falhou.'
  }
}
finally {
  Pop-Location
}

$serviceExecutable = Join-Path $distDirectory 'svoice_xtts_service\svoice_xtts_service.exe'
if (-not (Test-Path -LiteralPath $serviceExecutable)) {
  throw 'O executável do mecanismo XTTS não foi produzido.'
}

$selfTestProcess = Start-Process `
  -FilePath $serviceExecutable `
  -ArgumentList '--self-test' `
  -WindowStyle Hidden `
  -Wait `
  -PassThru
if ($selfTestProcess.ExitCode -ne 0) {
  throw 'O executável do mecanismo XTTS falhou no autoteste de dependências.'
}

Write-Output $serviceExecutable
