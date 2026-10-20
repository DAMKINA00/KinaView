param([string]$Iscc = 'ISCC.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $version = ([xml](Get-Content KinaView.csproj -Raw)).Project.PropertyGroup.Version
    $publish = Join-Path $root 'artifacts\win-x64'
    $release = Join-Path $root 'artifacts\release'
    # Refuse stale output rather than accidentally shipping removed files.
    if (Test-Path -LiteralPath $publish) { throw 'artifacts/win-x64 already exists; move it aside before rebuilding.' }
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    dotnet publish KinaView.csproj -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath 'Assets\KinaView.ico' -Destination $publish
    Copy-Item -LiteralPath 'licenses' -Destination $publish -Recurse
    foreach ($name in @('KinaView.exe','coreclr.dll','hostfxr.dll','PresentationFramework.dll','vcomp140.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $publish $name))) { throw "Missing runtime file: $name" }
    }
    $resizeRuntime = Join-Path $publish 'Runtime\RealESRGAN'
    foreach ($name in @('realesrgan-ncnn-vulkan.exe','vcomp140.dll','LICENSE.txt','MODEL-LICENSE.txt','NCNN-LICENSE.txt','LIBWEBP-LICENSE.txt','STB-LICENSE.txt','DIRENT-LICENSE.txt','PROVENANCE.md')) {
        if (!(Test-Path -LiteralPath (Join-Path $resizeRuntime $name))) { throw "Missing resize runtime file: $name" }
    }
    foreach ($scale in 2, 3, 4) {
        foreach ($extension in 'bin', 'param') {
            if (!(Test-Path -LiteralPath (Join-Path $resizeRuntime "models\realesr-animevideov3-x$scale.$extension"))) { throw "Missing resize model: x$scale.$extension" }
        }
    }
    $zip = Join-Path $release "KinaView-$version-Portable-x64.zip"
    Compress-Archive -Path "$publish\*" -DestinationPath $zip -CompressionLevel Optimal -Force
    & $Iscc "/DAppVersion=$version" 'installer\KinaView.iss'
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    Get-ChildItem -LiteralPath $release -File | Where-Object Extension -in '.exe','.zip' | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
} finally { Pop-Location }
