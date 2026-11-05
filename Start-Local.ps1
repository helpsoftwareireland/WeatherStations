#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)][int]$Port = 5080,
    [switch]$ShowApiKey,
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$previousKey = $env:Authentication__ApiKey
$apiProcess = $null
$runDirectory = Join-Path $PSScriptRoot ('.local/launcher/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$stdoutLog = Join-Path $runDirectory 'service.stdout.log'
$stderrLog = Join-Path $runDirectory 'service.stderr.log'
Push-Location $PSScriptRoot
try {
    if ([string]::IsNullOrWhiteSpace($previousKey)) {
        $randomBytes = New-Object byte[] 32
        $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $generator.GetBytes($randomBytes) } finally { $generator.Dispose() }
        $env:Authentication__ApiKey = [BitConverter]::ToString($randomBytes).Replace('-', '')
    }
    Write-Host 'Building the application...'
    & dotnet build Broadridge.sln --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $url = "http://127.0.0.1:$Port"
    $dll = Join-Path $PSScriptRoot 'src/WeatherAggregation.Api/bin/Release/net10.0/WeatherAggregation.Api.dll'
    $startOptions = @{
        FilePath = (Get-Command dotnet).Source
        ArgumentList = @(('"' + $dll + '"'), '--urls', $url)
        WorkingDirectory = $PSScriptRoot
        RedirectStandardOutput = $stdoutLog
        RedirectStandardError = $stderrLog
        PassThru = $true
    }
    if ($env:OS -eq 'Windows_NT') { $startOptions.WindowStyle = 'Hidden' }
    $apiProcess = Start-Process @startOptions
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $apiProcess.Refresh()
        if ($apiProcess.HasExited) { throw "Service failed to start. See $stdoutLog and $stderrLog" }
        # Match this process's startup log before probing, so an existing server on
        # the requested port cannot be mistaken for the server we just launched.
        if ((Test-Path -LiteralPath $stdoutLog) -and (Select-String -LiteralPath $stdoutLog -SimpleMatch "Now listening on: $url" -Quiet)) {
            try {
                $health = Invoke-WebRequest -UseBasicParsing -Uri "$url/health" -Headers @{ 'X-Api-Key' = $env:Authentication__ApiKey } -TimeoutSec 2
                if ($health.StatusCode -eq 200) { $ready = $true; break }
            } catch { }
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw "Service did not become ready. See $runDirectory" }
    Write-Host "Service: $url"
    Write-Host "Swagger: $url/docs/ (browser username: apikey)"
    Write-Host "Logs: $runDirectory"
    if ($ShowApiKey) { Write-Host "Local API key: $env:Authentication__ApiKey" }
    else { Write-Host 'API key is hidden. Supply your own environment key or use -ShowApiKey to display the temporary key locally.' }

    Write-Host 'Running the three-measurement example...'
    & dotnet run --project src/WeatherAggregation.Client --configuration Release --no-build -- --url $url --fixture true
    if ($LASTEXITCODE -ne 0) { throw 'Example client failed.' }
    if (-not $SmokeTest) {
        Write-Host 'Ready. Press Ctrl+C to stop the service.'
        while (-not $apiProcess.HasExited) { Start-Sleep -Seconds 1; $apiProcess.Refresh() }
        throw "Service exited unexpectedly. See $runDirectory"
    }
} finally {
    if ($null -ne $apiProcess) {
        $apiProcess.Refresh()
        if (-not $apiProcess.HasExited) { $apiProcess.Kill(); $apiProcess.WaitForExit() }
        $apiProcess.Dispose()
    }
    $env:Authentication__ApiKey = $previousKey
    Pop-Location
}
