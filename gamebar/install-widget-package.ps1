[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $elevatedArguments = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', ('"{0}"' -f $PSCommandPath)
    )
    $elevatedProcess = Start-Process powershell.exe -Verb RunAs -ArgumentList $elevatedArguments -Wait -PassThru
    exit $elevatedProcess.ExitCode
}

$package = Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter 'SVoice.GameBar*.msix' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$certificatePath = Join-Path $PSScriptRoot 'SVoice.GameBar.cer'
$sourceBridgeDirectory = Join-Path $PSScriptRoot 'bridge'
$sourceBridgePath = Join-Path $sourceBridgeDirectory 'SVoice.GameBarBridge.exe'
$xttsServicePath = Join-Path $env:ProgramFiles 'SVoice\xtts_service\svoice_xtts_service.exe'

if ($null -eq $package) {
    throw 'O pacote MSIX do SVoice Game Bar não foi encontrado nesta pasta.'
}

if (-not (Test-Path -LiteralPath $certificatePath)) {
    throw 'O certificado SVoice.GameBar.cer não foi encontrado nesta pasta.'
}

if (-not (Test-Path -LiteralPath $sourceBridgePath)) {
    throw 'O componente SVoice.GameBarBridge.exe não foi encontrado nesta pasta.'
}

if (-not (Test-Path -LiteralPath $xttsServicePath)) {
    throw 'O mecanismo XTTS não está instalado. Instale o SVoice completo antes do widget da Game Bar.'
}

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
$expectedThumbprint = '__SVOICE_CERT_THUMBPRINT__'
if ($certificate.Thumbprint -ne $expectedThumbprint) {
    throw 'O certificado não corresponde ao pacote oficial gerado para esta instalação.'
}

$trustedCertificate = Get-ChildItem -Path Cert:\LocalMachine\TrustedPeople |
    Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } |
    Select-Object -First 1

if ($null -eq $trustedCertificate) {
    Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
}

$signature = Get-AuthenticodeSignature -FilePath $package.FullName
if ($signature.Status -ne 'Valid' -or
    $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw 'A assinatura do pacote não corresponde ao certificado do SVoice.'
}

$bridgeInstallDirectory = Join-Path $env:LOCALAPPDATA 'SVoice\GameBarBridge'
New-Item -ItemType Directory -Path $bridgeInstallDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $sourceBridgeDirectory '*') -Destination $bridgeInstallDirectory -Recurse -Force
$installedBridgePath = Join-Path $bridgeInstallDirectory 'SVoice.GameBarBridge.exe'

$protocolRoot = 'HKCU:\Software\Classes\svoice-bridge'
New-Item -Path $protocolRoot -Force | Out-Null
Set-ItemProperty -Path $protocolRoot -Name '(Default)' -Value 'URL:SVoice Game Bar Bridge'
Set-ItemProperty -Path $protocolRoot -Name 'URL Protocol' -Value ''
$commandKey = New-Item -Path (Join-Path $protocolRoot 'shell\open\command') -Force
Set-ItemProperty -Path $commandKey.PSPath -Name '(Default)' -Value ('"{0}" "%1"' -f $installedBridgePath)

Get-Process -Name 'SVoice.GameBar' -ErrorAction SilentlyContinue | Stop-Process -Force

$existingPackage = Get-AppxPackage -Name 'SVoice.GameBar'
if ($null -ne $existingPackage) {
    $existingPackage | Remove-AppxPackage
}

Add-AppxPackage -Path $package.FullName -ForceApplicationShutdown

$catalogCheck = @'
$catalog = [Windows.ApplicationModel.AppExtensions.AppExtensionCatalog,Windows.ApplicationModel,ContentType=WindowsRuntime]::Open("microsoft.gameBarUIExtension")
$extension = $catalog.FindAll() | Where-Object { $_.Id -eq "SVoiceWidget" }
if ($null -eq $extension) {
    throw "A extensão SVoiceWidget não apareceu no catálogo da Game Bar."
}
Write-Host "SVoice Game Bar instalado. Abra Win + G e escolha SVoice no menu de widgets."
'@

& powershell.exe -NoProfile -Command $catalogCheck
if ($LASTEXITCODE -ne 0) {
    throw 'O pacote foi instalado, mas o registro do widget não pôde ser validado.'
}
