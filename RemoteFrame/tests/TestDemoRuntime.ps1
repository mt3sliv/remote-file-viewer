param(
    [string]$Configuration = 'Debug',
    [string]$FreshWorkerDirectory = "$PSScriptRoot/../artifacts/windows-worker",
    [string]$ServerOutputDirectory,
    [string]$ExpectedPackageVersion,
    [switch]$UsePackageWorker
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$server = Join-Path $root 'src/RemoteFrame.Server'
[xml]$project = Get-Content -LiteralPath "$server/RemoteFrame.Server.csproj" -Raw
$expectedVersion = if ($ExpectedPackageVersion) { $ExpectedPackageVersion } else {
    $project.SelectSingleNode('/Project/PropertyGroup/RemoteFramePackageVersion').InnerText
}
$assets = Get-Content -LiteralPath "$server/obj/project.assets.json" -Raw | ConvertFrom-Json -AsHashtable
foreach ($package in 'RemoteFrame.Office.Windows', 'RemoteFrame.Office.Runtime.win-x64') {
    if (-not $assets.libraries.ContainsKey("$package/$expectedVersion")) {
        throw "The demo did not restore $package/$expectedVersion. Restore/build it with default package references."
    }
}
if ($UsePackageWorker) {
    $FreshWorkerDirectory = $assets.packageFolders.Keys | ForEach-Object {
        Join-Path $_ "remoteframe.office.runtime.win-x64/$expectedVersion/tools/remote-frame/win-x64/worker"
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (-not $FreshWorkerDirectory) { throw "Cannot locate the restored runtime package $expectedVersion." }
}
if (-not $ServerOutputDirectory) { $ServerOutputDirectory = Join-Path $server "bin/$Configuration/net10.0" }
foreach ($package in 'RemoteFrame.AspNetCore','RemoteFrame.Core','RemoteFrame.Rendering') {
    $matches = @($assets.libraries.Keys | Where-Object { $_.StartsWith("$package/",[StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -ne 1) { throw "Cannot identify the restored managed package $package." }
    $relative = $assets.libraries[$matches[0]].path
    $packed = $assets.packageFolders.Keys | ForEach-Object {
        Join-Path $_ "$relative/lib/net10.0/$package.dll"
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $packed -or (Get-FileHash -LiteralPath $packed).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $ServerOutputDirectory "$package.dll")).Hash) {
        throw "The demo has a stale managed library: $package"
    }
}
$runtime = Join-Path $ServerOutputDirectory 'remote-frame/win-x64'
$manifest = Get-Content -LiteralPath "$runtime/runtime.json" -Raw | ConvertFrom-Json -AsHashtable
foreach ($name in 'RemoteFrame.Office.Worker.exe','RemoteFrame.Office.Worker.dll','RemoteFrame.Core.dll','RemoteFrame.Rendering.dll','RemoteFrame.InputHook.dll') {
    $expected = (Get-FileHash -LiteralPath (Join-Path $FreshWorkerDirectory $name)).Hash
    $actual = (Get-FileHash -LiteralPath "$runtime/worker/$name").Hash
    if ($actual -ne $expected -or $manifest.files["worker/$name"] -ne $expected) {
        throw "The demo has a stale or incorrectly manifested worker dependency: $name"
    }
}
Write-Output "PASS: Demo restores $expectedVersion and deploys the expected worker, libraries and input helper with matching manifest hashes."
