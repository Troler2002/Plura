# Genera el paquete de publicación para Partner Center (.msixupload).
# IMPORTANTE: antes de ejecutarlo, Package.appxmanifest debe tener la identidad
# (Name / Publisher / PublisherDisplayName) asignada por la Store.
#
# Se compila desde una copia en %TEMP% porque .NET Native (ilc.exe) falla cuando
# el origen está sincronizado con OneDrive.

param(
    [string]$MsBuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\StorePackages')
)

$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$workRoot = Join-Path $env:TEMP 'plura-store-build'
$workDir = Join-Path $workRoot 'XboxMultiStreamNative'
$out = [IO.Path]::GetFullPath($OutDir)

Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
New-Item -ItemType Directory -Force -Path $out | Out-Null

& robocopy.exe $projectDir $workDir /E /XD bin obj AppPackages StorePackages /XF *.log /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy falló con código $LASTEXITCODE" }

$proj = Join-Path $workDir 'Plura.csproj'

& $MsBuild $proj /t:Restore /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Restore falló' }

& $MsBuild $proj /t:Build `
    /p:Configuration=Release `
    /p:Platform=x64 `
    /p:AppxBundle=Always `
    /p:AppxBundlePlatforms=x64 `
    /p:UapAppxPackageBuildMode=StoreUpload `
    /p:AppxPackageSigningEnabled=false `
    /p:PackageCertificateKeyFile= `
    /p:PackageCertificateThumbprint= `
    /p:AppxPackageDir=$out `
    /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Build falló' }

Get-ChildItem -Path $out -Recurse -Filter *.msixupload |
    Select-Object FullName, Length |
    Format-Table -AutoSize
