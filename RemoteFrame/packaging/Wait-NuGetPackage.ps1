param(
    [Parameter(Mandatory)][ValidatePattern('^RemoteFrame\.[A-Za-z0-9.-]+$')][string]$PackageId,
    [Parameter(Mandatory)][string]$Version,
    [ValidateRange(1,120)][int]$MaxAttempts = 20,
    [ValidateRange(0,60)][int]$RetrySeconds = 15,
    [scriptblock]$Probe
)
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
if (-not $Probe) {
    $Probe = {
        param($id, $version)
        $base = "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())"
        try {
            $index = Invoke-RestMethod "$base/index.json" -TimeoutSec 10
            if ($version.ToLowerInvariant() -notin $index.versions) { return $false }
            $name = $id.ToLowerInvariant()
            $normalized = $version.ToLowerInvariant()
            $response = Invoke-WebRequest "$base/$normalized/$name.$normalized.nupkg" -Method Head -SkipHttpErrorCheck -TimeoutSec 10
            return $response.StatusCode -eq 200
        }
        catch { Write-Verbose $_.Exception.Message; return $false }
    }
}
for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    if (& $Probe $PackageId $Version) {
        Write-Output "Available from NuGet.org: $PackageId $Version"
        return
    }
    if ($attempt -lt $MaxAttempts) {
        Write-Output "Waiting for NuGet validation/indexing: $PackageId $Version ($attempt/$MaxAttempts)"
        if ($RetrySeconds) { Start-Sleep -Seconds $RetrySeconds }
    }
}
throw "$PackageId $Version was uploaded or already existed, but is not available for restore from NuGet.org. Dependent publication stopped. Check NuGet.org Manage Packages for pending/failed validation, then rerun the publish job using the existing artifacts."
