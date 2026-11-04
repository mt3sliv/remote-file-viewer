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
    if ($_.Exception.Message -notlike '*Release verification failed*') { throw }
    $rejected = $true
}
if (-not $rejected -or $state.Calls -ne 2) { throw 'Unavailable dependency did not fail within the attempt limit.' }
$state.Calls = 0
$slowProbe = { param($id, $version) $state.Calls++; Start-Sleep -Milliseconds 1100; return $false }.GetNewClosure()
$rejected = $false
try {
    & "$root/packaging/Wait-NuGetPackage.ps1" -PackageId RemoteFrame.Office.Worker.Runtime.win-x64.Part001 -Version 0.3.0-preview.14 -TimeoutSeconds 1 -RetrySeconds 0 -Probe $slowProbe
} catch {
    if ($_.Exception.Message -notlike '*Release verification failed*') { throw }
    $rejected = $true
}
if (-not $rejected -or $state.Calls -ne 1) { throw 'Elapsed-time budget did not stop polling.' }

# Exercise upload ordering without network calls or a real publishing key.
$fixture = Join-Path $root ('artifacts/availability-policy/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixture | Out-Null
foreach ($script in 'Publish-NuGet.ps1','Get-ReleaseVersion.ps1','Get-ReleasePackagePolicy.ps1') {
    Copy-Item "$root/packaging/$script" "$fixture/$script"
}
$state.Uploads = 0
$state.Verifications = 0
@'
param($PackageId, $Version, $TimeoutSeconds)
if ($state.Uploads -ne 3) { throw 'Availability was checked before all managed packages were uploaded.' }
if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 60) { throw 'Shared availability budget was not forwarded.' }
$state.Verifications++
'@ | Set-Content "$fixture/Wait-NuGetPackage.ps1"
$version = '0.0.0-availability'
$entries = @(foreach ($id in 'RemoteFrame.Core','RemoteFrame.Rendering','RemoteFrame.AspNetCore') {
    $file = "$id.$version.nupkg"
    Set-Content "$fixture/$file" 'TEXT FIXTURE ONLY'
    @{id=$id;file=$file;bytes=(Get-Item "$fixture/$file").Length;sha256=(Get-FileHash "$fixture/$file").Hash;dependencies=@();dependencyVersions=@{}}
})
@{version=$version;group='Managed';commit=('a' * 40);packages=$entries} | ConvertTo-Json -Depth 5 | Set-Content "$fixture/release-Managed.json"
function dotnet {
    if ($args[0] -ne 'nuget' -or $args[1] -ne 'push') { throw 'Unexpected dotnet invocation.' }
    $state.Uploads++
    $global:LASTEXITCODE = 0
}
$previousKey = $env:NUGET_API_KEY
try {
    $env:NUGET_API_KEY = 'TEST-FIXTURE-NOT-A-REAL-KEY'
    & "$fixture/Publish-NuGet.ps1" -Version $version -Directory $fixture -Group Managed -ExpectedCommit ('a' * 40) -AvailabilityTimeoutSeconds 60
} finally {
    $env:NUGET_API_KEY = $previousKey
    Remove-Item Function:dotnet
}
if ($state.Uploads -ne 3 -or $state.Verifications -ne 3) { throw 'The whole uploaded group was not verified.' }
Write-Output 'PASS: NuGet availability checks handle indexing delays and stop on missing packages.'
