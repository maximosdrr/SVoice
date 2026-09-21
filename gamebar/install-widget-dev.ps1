<#
.SYNOPSIS
Registers the Debug build of the widget for development (loose files).

.DESCRIPTION
The bridge inside the registered package locates the XTTS service in this
repository (service\svoice_xtts_service.py) and uses the development virtual
environment (python_service\.build-venv) or the SVOICE_XTTS_PYTHON override.
Widget settings are preserved when re-registering the same version.
#>
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

Get-Process -Name 'SVoice.GameBar', 'SVoice.GameBarBridge' -ErrorAction SilentlyContinue | Stop-Process -Force

$existingPackage = Get-AppxPackage -Name 'SVoice.GameBar'
if ($null -ne $existingPackage -and -not $existingPackage.IsDevelopmentMode) {
    # A signed installation cannot be replaced by a loose registration in place.
    $existingPackage | Remove-AppxPackage -PreserveApplicationData
}

Add-AppxPackage -Register $manifestPath -ForceApplicationShutdown -ForceUpdateFromAnyVersion

$catalog = [Windows.ApplicationModel.AppExtensions.AppExtensionCatalog, Windows.ApplicationModel, ContentType = WindowsRuntime]::Open('microsoft.gameBarUIExtension')
$operation = $catalog.FindAllAsync()
$deadline = (Get-Date).AddSeconds(30)
while ($operation.Status -eq 'Started' -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
}
$extension = $null
if ($operation.Status -eq 'Completed') {
    $extension = $operation.GetResults() | Where-Object { $_.Id -eq 'SVoiceWidget' }
}

if ($null -eq $extension) {
    throw 'A extensão SVoiceWidget não apareceu no catálogo da Game Bar.'
}

Write-Host "SVoiceWidget registrado ($($extension.Package.Id.FullName)). Abra Win + G e escolha SVoice no menu de widgets."
