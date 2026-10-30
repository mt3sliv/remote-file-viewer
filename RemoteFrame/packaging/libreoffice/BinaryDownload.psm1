Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-VerifiedBinaryDownload {
    param([Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Sha256, [ValidateRange(1,3)][int]$Attempts = 3,
        [ValidateRange(0,30)][int]$RetryDelaySeconds = 5,
        [scriptblock]$Transport = {
            param($url,$path)
            & curl.exe --fail --location --silent --show-error --connect-timeout 30 --max-time 600 `
                --speed-limit 1024 --speed-time 60 --retry 0 --output $path $url
            if ($LASTEXITCODE -ne 0) { throw "Download transport failed with curl exit code $LASTEXITCODE." }
        })
    if ($Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Expected a pinned SHA-256 digest.' }
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        Write-Host "Downloading pinned LibreOffice archive, attempt $attempt/$Attempts (10-minute limit; stalled transfers abort after 60 seconds)..."
        try {
            & $Transport $Uri $Destination
            if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) { throw 'Downloaded LibreOffice archive failed checksum verification.' }
            Write-Host 'LibreOffice download completed and its SHA-256 digest matches the lock.'
            return
        } catch {
            if ($attempt -eq $Attempts) { throw "LibreOffice download failed after $Attempts bounded attempts: $($_.Exception.Message)" }
            Write-Warning "Download attempt failed: $($_.Exception.Message). Retrying..."
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }
}

function Wait-BinaryExtraction {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process,
        [ValidateRange(1,1800)][int]$TimeoutSeconds = 1200)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $Process.HasExited) {
        $remaining = $TimeoutSeconds * 1000 - $timer.ElapsedMilliseconds
        if ($remaining -le 0) {
            $Process.Kill($true)
            $null = $Process.WaitForExit(5000)
            throw "LibreOffice administrative extraction timed out after $TimeoutSeconds seconds. See its MSI log."
        }
        if (-not $Process.WaitForExit([int][Math]::Min(30000,$remaining))) {
            Write-Host "LibreOffice extraction is still running ($([int]$timer.Elapsed.TotalSeconds) seconds)..."
        }
    }
    return $Process.ExitCode
}
Export-ModuleMember -Function Invoke-VerifiedBinaryDownload,Wait-BinaryExtraction
