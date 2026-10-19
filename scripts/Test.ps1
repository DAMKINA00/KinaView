param([string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $samples = Join-Path $root 'evidence\format-samples'
    New-Item -ItemType Directory -Path $samples -Force | Out-Null
    foreach ($sample in (Get-Content "$PSScriptRoot\test-samples.json" -Raw | ConvertFrom-Json)) {
        $path = Join-Path $samples $sample.File
        if (!(Test-Path -LiteralPath $path)) { Invoke-WebRequest $sample.URL -OutFile $path }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $sample.SHA256) { throw "Sample checksum mismatch: $($sample.File)" }
    }
    if (!$Executable) {
        dotnet build -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
        $Executable = Join-Path $root 'bin\Release\net10.0-windows\KinaView.exe'
    }
    $output = Join-Path $root ('evidence\qa-' + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process -FilePath $Executable -ArgumentList '--self-test', ('"' + $output + '"') -WindowStyle Hidden -PassThru -Wait
    $report = Get-Content (Join-Path $output 'tests.json') -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or !$report.passed) { throw "Self-test failed. See $output/tests.json" }
    Write-Output "$($report.count) checks passed. Report: $output/tests.json"
} finally { Pop-Location }
