$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$state = @{ Calls = 0; AvailableAfter = 3 }
$probe = {
    param($id, $version)
    if ($id -ne 'RemoteFrame.Office.Worker.Runtime.win-x64.Part001' -or $version -ne '0.3.0-preview.14') { throw 'Unexpected package probe.' }
    $state.Calls++
    return $state.Calls -ge $state.AvailableAfter
}.GetNewClosure()
& "$root/packaging/Wait-NuGetPackage.ps1" -PackageId RemoteFrame.Office.Worker.Runtime.win-x64.Part001 -Version 0.3.0-preview.14 -MaxAttempts 3 -RetrySeconds 0 -Probe $probe
if ($state.Calls -ne 3) { throw 'Did not wait for delayed availability.' }
$state.Calls = 0
$state.AvailableAfter = 1
& "$root/packaging/Wait-NuGetPackage.ps1" -PackageId RemoteFrame.Office.Worker.Runtime.win-x64.Part001 -Version 0.3.0-preview.14 -MaxAttempts 3 -RetrySeconds 0 -Probe $probe
if ($state.Calls -ne 1) { throw 'Available packages should not be retried.' }
$state.Calls = 0
$state.AvailableAfter = 99
$rejected = $false
try {
    & "$root/packaging/Wait-NuGetPackage.ps1" -PackageId RemoteFrame.Office.Worker.Runtime.win-x64.Part001 -Version 0.3.0-preview.14 -MaxAttempts 2 -RetrySeconds 0 -Probe $probe
} catch {
    if ($_.Exception.Message -notlike '*Dependent publication stopped*') { throw }
    $rejected = $true
}
if (-not $rejected -or $state.Calls -ne 2) { throw 'Unavailable dependency did not fail within the attempt limit.' }
Write-Output 'PASS: NuGet availability checks handle indexing delays and stop on missing packages.'
