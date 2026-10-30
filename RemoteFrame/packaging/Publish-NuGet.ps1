param([Parameter(Mandatory)][string]$Version,[string]$Directory = "$PSScriptRoot/../artifacts/packages",[switch]$ValidateOnly)
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
$manifest = Get-Content "$Directory/release.json" -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version) { throw 'Artifact version does not match the release tag.' }
if ($env:GITHUB_SHA -and $manifest.commit -ne $env:GITHUB_SHA) { throw 'Artifacts came from a different commit.' }
$order = @('RemoteFrame.Core','RemoteFrame.Rendering','RemoteFrame.AspNetCore','RemoteFrame.Office.Runtime.win-x64','RemoteFrame.Office.Windows')
foreach ($required in $order) {
    if (@($manifest.packages | Where-Object id -EQ $required).Count -ne 1) { throw "Missing or duplicate release package: $required" }
}
if (@($manifest.packages | Where-Object id -Match '\.Part[0-9]{3}$').Count -eq 0) { throw 'Missing runtime dependency parts.' }
if (@($manifest.packages.id | Sort-Object -Unique).Count -ne @($manifest.packages).Count) { throw 'Duplicate package identities.' }
foreach ($entry in $manifest.packages) {
    foreach ($dependency in $entry.dependencies) {
        if ($dependency -notin $manifest.packages.id) { throw "Missing release dependency: $dependency" }
    }
    if ($entry.file -ne [IO.Path]::GetFileName($entry.file)) { throw 'Unsafe artifact file name.' }
    if ($entry.id -notin $order -and $entry.id -notmatch '^RemoteFrame\.Office\.Runtime\.win-x64\.Part[0-9]{3}$') { throw 'Unexpected package identity.' }
    if ($entry.file -ne "$($entry.id).$Version.nupkg") { throw 'Package file name does not match its identity and release version.' }
    $file = Get-Item -LiteralPath "$Directory/$($entry.file)"
    if ($file.Length -ge 240000000 -or $file.Length -ne $entry.bytes -or (Get-FileHash $file.FullName).Hash -ne $entry.sha256) {
        throw "Artifact integrity or size failure: $($entry.file)"
    }
}
if ($ValidateOnly) { Write-Output 'PASS: Release artifacts match their version, sizes and hashes.'; return }
if (-not $env:NUGET_API_KEY) { throw 'Obtain a temporary NUGET_API_KEY through NuGet/login immediately before publishing.' }
$packages = $manifest.packages | Sort-Object @{Expression={ if ($_.id -match '\.Part[0-9]{3}$') { -1 } else { [array]::IndexOf($order,$_.id) } }},id
foreach ($entry in $packages) {
    & dotnet nuget push "$Directory/$($entry.file)" --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY --skip-duplicate --timeout 900
    if ($LASTEXITCODE -ne 0) { throw "NuGet publish failed: $($entry.id). Rerun the publish job to retry existing artifacts." }
}
