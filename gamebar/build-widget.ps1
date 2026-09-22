[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'SVoice.GameBar\SVoice.GameBar.csproj'
$bridgeProjectPath = Join-Path $PSScriptRoot 'SVoice.GameBarBridge\SVoice.GameBarBridge.csproj'
$bridgePackageDirectory = Join-Path $PSScriptRoot 'SVoice.GameBar\Bridge'
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$buildStartedAt = Get-Date

if (-not (Test-Path -LiteralPath $vswherePath)) {
    throw 'Visual Studio Installer (vswhere.exe) não foi encontrado.'
}

$visualStudioPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ([string]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw 'Visual Studio com MSBuild não foi encontrado.'
}

$msbuildPath = Join-Path $visualStudioPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuildPath)) {
    throw "MSBuild não foi encontrado em $msbuildPath"
}

& dotnet publish $bridgeProjectPath `
    "-c=$Configuration" `
    '-r=win-x64' `
    '--self-contained=true' `
    '-p:Platform=x64' `
    '-p:PublishSingleFile=true' `
    '-p:DebugType=None' `
    '-p:DebugSymbols=false' `
    "-o=$bridgePackageDirectory" `
    '--nologo'
if ($LASTEXITCODE -ne 0) {
    throw "A publicação do bridge XTTS falhou com código $LASTEXITCODE."
}

& $msbuildPath $projectPath '/t:Rebuild' "/p:Configuration=$Configuration" '/p:Platform=x64' '/p:RuntimeIdentifier=win-x64' '/p:SelfContained=true' '/p:GenerateAppxPackageOnBuild=true' '/p:AppxPackageSigningEnabled=false' '/m' '/restore' '/v:minimal'
if ($LASTEXITCODE -ne 0) {
    throw "A compilação do widget falhou com código $LASTEXITCODE."
}

$packageRoot = Join-Path $PSScriptRoot 'SVoice.GameBar\AppPackages'
$packageCandidates = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.msix' |
    Where-Object { $_.LastWriteTime -ge $buildStartedAt.AddSeconds(-2) }

if ($Configuration -eq 'Debug') {
    $packageCandidates = $packageCandidates | Where-Object { $_.FullName -match '_Debug_' }
}
else {
    $packageCandidates = $packageCandidates | Where-Object { $_.FullName -notmatch '_Debug_' }
}

$package = $packageCandidates |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $package) {
    throw 'A compilação terminou, mas nenhum pacote MSIX foi encontrado.'
}

# A máquina de desenvolvimento já possui o .NET 10 e pode esconder uma
# publicação dependente de framework. O widget é distribuído diretamente por
# nosso instalador, então o MSIX precisa carregar o runtime completo.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    $requiredRuntimeFiles = @('coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll')
    $missingRuntimeFiles = @($requiredRuntimeFiles | Where-Object { $_ -notin $entryNames })
    if ($missingRuntimeFiles.Count -gt 0) {
        throw "O MSIX não é autocontido; arquivos ausentes: $($missingRuntimeFiles -join ', ')."
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Widget compilado: $($package.FullName)"
