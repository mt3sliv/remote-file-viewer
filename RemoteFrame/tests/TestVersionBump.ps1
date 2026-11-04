$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('artifacts/version-bump/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$fixture/packaging" | Out-Null
foreach ($script in 'Bump-Version.ps1','Get-ReleaseVersion.ps1') {
    Copy-Item "$root/packaging/$script" "$fixture/packaging/$script"
}
$props = "$fixture/Directory.Build.props"
function Set-Fixture([string]$version) {
    [IO.File]::WriteAllText($props, '<Project><PropertyGroup><Version Condition="test">' + $version + '</Version><RemoteFramePackageVersion>$(Version)</RemoteFramePackageVersion></PropertyGroup></Project>')
}
function Expect([string]$version) {
    if ((& "$fixture/packaging/Get-ReleaseVersion.ps1") -ne $version) { throw "Expected version $version." }
    if ([IO.File]::ReadAllText($props) -notlike '*<RemoteFramePackageVersion>$(Version)</RemoteFramePackageVersion>*') { throw 'Dependent version property was changed.' }
}
Set-Fixture '0.3.0-preview.11'
& "$fixture/packaging/Bump-Version.ps1" -WhatIf
Expect '0.3.0-preview.11'
& "$fixture/packaging/Bump-Version.ps1"
Expect '0.3.0-preview.12'
& "$fixture/packaging/Bump-Version.ps1"
Expect '0.3.0-preview.13'
Set-Fixture '1.2.9'
& "$fixture/packaging/Bump-Version.ps1"
Expect '1.2.10'
& "$fixture/packaging/Bump-Version.ps1" -Version '2.0.0-preview.1'
Expect '2.0.0-preview.1'
foreach ($invalid in 'v1.2.3','1.2.3-preview.01','2.0.0-preview.1') {
    $rejected = $false
    try { & "$fixture/packaging/Bump-Version.ps1" -Version $invalid | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid or unchanged version: $invalid" }
    Expect '2.0.0-preview.1'
}
Set-Fixture '1.2.3-beta'
$rejected = $false
try { & "$fixture/packaging/Bump-Version.ps1" | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Accepted ambiguous prerelease increment.' }
Expect '1.2.3-beta'
Write-Output 'PASS: Version increments, explicit versions, preview safety, and rejection cases.'
