$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($version in '1.2.3','0.3.0-preview.8','0.3.0-source.2','2.0.0-alpha-beta.1') {
    if ((& "$root/packaging/Get-ReleaseVersion.ps1" $version) -ne $version) { throw 'Release version changed unexpectedly.' }
}
function Reject([scriptblock]$action,[string]$name) {
    try { & $action | Out-Null } catch { Write-Output "PASS: $name rejected"; return }
    throw "$name was accepted unexpectedly."
}
foreach ($version in 'v1.2.3','01.2.3','1.2','1.2.3-preview.01','1.2.3;echo bad','../1.2.3') {
    Reject { & "$root/packaging/Get-ReleaseVersion.ps1" $version } "Invalid release version $version"
}
$test = Join-Path $root ('artifacts/release-policy/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $test | Out-Null
$version = '0.0.0-policy'
# Only text fixtures and ValidateOnly: this test never authenticates or publishes.
$entries = @(foreach ($id in 'RemoteFrame.Core','RemoteFrame.Rendering','RemoteFrame.AspNetCore','RemoteFrame.Office.Runtime.win-x64','RemoteFrame.Office.Windows','RemoteFrame.Office.Runtime.win-x64.Part001') {
    $file = "$id.$version.nupkg"
    Set-Content "$test/$file" 'TEXT FIXTURE ONLY'
    @{id=$id;file=$file;bytes=(Get-Item "$test/$file").Length;sha256=(Get-FileHash "$test/$file").Hash}
})
$manifest = @{version=$version;commit=(& git rev-parse HEAD);packages=$entries}
function Save-Manifest { $manifest | ConvertTo-Json -Depth 5 | Set-Content "$test/release.json" }
Save-Manifest
& "$root/packaging/Publish-NuGet.ps1" -Version $version -Directory $test -ValidateOnly
$entries[0].sha256 = '0' * 64; Save-Manifest
Reject { & "$root/packaging/Publish-NuGet.ps1" -Version $version -Directory $test -ValidateOnly } 'Changed artifact hash'
$entries[0].sha256 = (Get-FileHash "$test/$($entries[0].file)").Hash
$entries[0].file = '../escaped.nupkg'; Save-Manifest
Reject { & "$root/packaging/Publish-NuGet.ps1" -Version $version -Directory $test -ValidateOnly } 'Artifact path traversal'
$entries[0].file = "RemoteFrame.Core.$version.nupkg"
$manifest.packages = @($entries | Select-Object -Skip 1); Save-Manifest
Reject { & "$root/packaging/Publish-NuGet.ps1" -Version $version -Directory $test -ValidateOnly } 'Incomplete release set'
Write-Output 'PASS: Release versions and artifact integrity checks.'
$invalid = "$test/invalid-license"
New-Item -ItemType Directory -Force $invalid | Out-Null
$zip = [IO.Compression.ZipFile]::Open("$invalid/RemoteFrame.Core.$version.nupkg",[IO.Compression.ZipArchiveMode]::Create)
try {
    $writer = [IO.StreamWriter]::new($zip.CreateEntry('RemoteFrame.Core.nuspec').Open())
    try { $writer.Write("<package><metadata><id>RemoteFrame.Core</id><version>$version</version><license type='file'>license.txt</license><readme>README.md</readme></metadata></package>") } finally { $writer.Dispose() }
    $writer = [IO.StreamWriter]::new($zip.CreateEntry('README.md').Open())
    try { $writer.Write("# RemoteFrame.Core`n`nTEST FIXTURE ONLY") } finally { $writer.Dispose() }
    $writer = [IO.StreamWriter]::new($zip.CreateEntry('license.txt').Open())
    try { $writer.Write('x' * 1100000) } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
$rejected = $false
try { & "$root/packaging/New-ReleaseManifest.ps1" -Version $version -Directory $invalid | Out-Null }
catch {
    if ($_.Exception.Message -notlike '*license metadata*1 MB*') { throw }
    $rejected = $true
}
if (-not $rejected) { throw 'Oversized NuGet license metadata was accepted.' }
Write-Output 'PASS: NuGet license metadata above 1 MB is rejected before publication.'
