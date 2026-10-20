$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$archive = Join-Path $root 'artifacts\resize-download\realesrgan-windows.zip'
$expected = 'ABC02804E17982A3BE33675E4D471E91EA374E65B70167ABC09E31ACB412802D'
New-Item -ItemType Directory -Path (Split-Path $archive) -Force | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Real-ESRGAN package checksum mismatch.' }
$destination = [IO.Path]::GetFullPath((Join-Path $root 'Runtime\RealESRGAN'))
New-Item -ItemType Directory -Path (Join-Path $destination 'models') -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $files = @('realesrgan-ncnn-vulkan.exe', 'vcomp140.dll', 'README_windows.md')
    foreach ($scale in 2, 3, 4) { $files += "models/realesr-animevideov3-x$scale.bin", "models/realesr-animevideov3-x$scale.param" }
    foreach ($name in $files) {
        $target = [IO.Path]::GetFullPath((Join-Path $destination $name))
        if (!$target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid output path.' }
        $entry = $zip.GetEntry($name)
        if (!$entry) { throw "Missing model package entry: $name" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $zip.Dispose() }
Write-Output "Real-ESRGAN runtime and 2x/3x/4x anime models: $destination"
