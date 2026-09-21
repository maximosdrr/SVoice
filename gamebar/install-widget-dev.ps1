[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $PSScriptRoot 'SVoice.GameBar\bin\x64\Debug\net10.0-windows10.0.26100.0\AppxManifest.xml'
$bridgePath = Join-Path $PSScriptRoot 'SVoice.GameBar\Bridge\SVoice.GameBarBridge.exe'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'A compilação Debug não foi encontrada. Execute .\gamebar\build-widget.ps1 primeiro.'
}

if (-not (Test-Path -LiteralPath $bridgePath)) {
    throw 'O bridge XTTS Debug não foi encontrado. Execute .\gamebar\build-widget.ps1 primeiro.'
}

Get-Process -Name 'SVoice.GameBar' -ErrorAction SilentlyContinue | Stop-Process -Force

$existingPackage = Get-AppxPackage -Name 'SVoice.GameBar'
if ($null -ne $existingPackage) {
    # Windows blocks re-registering a development package with the same version,
    # even when its loose files changed. Remove only this package and add it back.
    $existingPackage | Remove-AppxPackage
}

Add-AppxPackage -Register $manifestPath -ForceApplicationShutdown -ForceUpdateFromAnyVersion

$protocolRoot = 'HKCU:\Software\Classes\svoice-bridge'
New-Item -Path $protocolRoot -Force | Out-Null
Set-ItemProperty -Path $protocolRoot -Name '(Default)' -Value 'URL:SVoice Game Bar Bridge'
Set-ItemProperty -Path $protocolRoot -Name 'URL Protocol' -Value ''
$commandKey = New-Item -Path (Join-Path $protocolRoot 'shell\open\command') -Force
Set-ItemProperty -Path $commandKey.PSPath -Name '(Default)' -Value ('"{0}" "%1"' -f $bridgePath)

$catalogCheck = @'
$catalog = [Windows.ApplicationModel.AppExtensions.AppExtensionCatalog,Windows.ApplicationModel,ContentType=WindowsRuntime]::Open("microsoft.gameBarUIExtension")
$extension = $catalog.FindAll() | Where-Object { $_.Id -eq "SVoiceWidget" }
if ($null -eq $extension) {
    throw "A extensão SVoiceWidget não apareceu no catálogo da Game Bar."
}
Write-Host "SVoiceWidget registrado e disponível no menu da Xbox Game Bar."
'@

& powershell.exe -NoProfile -Command $catalogCheck
if ($LASTEXITCODE -ne 0) {
    throw 'Não foi possível validar o registro do widget.'
}
