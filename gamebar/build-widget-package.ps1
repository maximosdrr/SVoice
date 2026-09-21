[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $projectDirectory 'artifacts\gamebar'
$buildScript = Join-Path $PSScriptRoot 'build-widget.ps1'
$installerScript = Join-Path $PSScriptRoot 'install-widget-package.ps1'
$manifestPath = Join-Path $PSScriptRoot 'SVoice.GameBar\Package.appxmanifest'
$bridgeProject = Join-Path $PSScriptRoot 'SVoice.GameBarBridge\SVoice.GameBarBridge.csproj'
$subject = 'CN=SVoice Development'

[xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
$packageVersion = $manifest.Package.Identity.Version

& $buildScript -Configuration Release

$packageRoot = Join-Path $PSScriptRoot 'SVoice.GameBar\AppPackages'
$sourcePackage = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.msix' |
    Where-Object { $_.FullName -notmatch '_Debug_' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $sourcePackage) {
    throw 'O pacote Release do widget não foi encontrado.'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$bridgeOutputDirectory = Join-Path $outputDirectory 'bridge'
$packagePath = Join-Path $outputDirectory "SVoice.GameBar_$($packageVersion)_x64.msix"
$certificatePath = Join-Path $outputDirectory 'SVoice.GameBar.cer'
$outputInstallerPath = Join-Path $outputDirectory 'Install-SVoice-GameBar.ps1'

Copy-Item -LiteralPath $sourcePackage.FullName -Destination $packagePath -Force

& dotnet publish $bridgeProject `
    '-c=Release' `
    '-r=win-x64' `
    '--self-contained=true' `
    '-p:Platform=x64' `
    '-p:PublishSingleFile=true' `
    '-p:DebugType=None' `
    '-p:DebugSymbols=false' `
    "-o=$bridgeOutputDirectory" `
    '--nologo'
if ($LASTEXITCODE -ne 0) {
    throw "A publicação do bridge XTTS falhou com código $LASTEXITCODE."
}

$certificate = Get-ChildItem -Path Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $subject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddMonths(1)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -FriendlyName 'SVoice Game Bar Development' `
        -KeyUsage DigitalSignature `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -NotAfter (Get-Date).AddYears(3)
}

Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null

$installerContent = Get-Content -Raw -LiteralPath $installerScript
$installerContent = $installerContent.Replace('__SVOICE_CERT_THUMBPRINT__', $certificate.Thumbprint)
Set-Content -LiteralPath $outputInstallerPath -Value $installerContent -Encoding utf8

$windowsKitBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signToolPath = Get-ChildItem -LiteralPath $windowsKitBin -Directory |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($signToolPath)) {
    throw 'SignTool não foi encontrado no Windows SDK.'
}

& $signToolPath sign /fd SHA256 /sha1 $certificate.Thumbprint $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "A assinatura do MSIX falhou com código $LASTEXITCODE."
}

$signature = Get-AuthenticodeSignature -FilePath $packagePath
if ($null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw 'O MSIX foi gerado, mas a assinatura não pôde ser confirmada.'
}

Write-Host "Pacote instalável criado em: $outputDirectory"
