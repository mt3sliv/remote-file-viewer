param([Parameter(Mandatory)][string]$Version,[string]$Directory = "$PSScriptRoot/../artifacts/packages")
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
& "$PSScriptRoot/../tests/TestPackageReadmes.ps1" -Directory $Directory -Version $Version
$items = @(foreach ($file in Get-ChildItem $Directory -Filter "*.$Version.nupkg") {
    if ($file.Length -ge 240000000) { throw "Package exceeds the 240 MB release ceiling: $($file.Name)" }
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entry = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entry.Count -ne 1) { throw 'Invalid package metadata.' }
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $id = [string]$spec.package.metadata.id
        if ($spec.package.metadata.version -ne $Version -or $id -notlike 'RemoteFrame.*') { throw 'Unexpected release package identity.' }
        $licenseNode = $spec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license' and @type='file']")
        if ($licenseNode) {
            $licenseEntry = $zip.GetEntry($licenseNode.InnerText)
            if (-not $licenseEntry -or $licenseEntry.Length -ge 1000000) { throw "NuGet license metadata is missing or exceeds the 1 MB limit: $id" }
        }
        $dependencies = @($spec.SelectNodes("//*[local-name()='dependency']") | ForEach-Object { [string]$_.id } | Where-Object { $_ -like 'RemoteFrame.*' })
        [ordered]@{ id=$id; file=$file.Name; sha256=(Get-FileHash $file.FullName).Hash; bytes=$file.Length; dependencies=$dependencies }
    } finally { $zip.Dispose() }
})
foreach ($required in 'RemoteFrame.Core','RemoteFrame.Rendering','RemoteFrame.AspNetCore','RemoteFrame.Office.Runtime.win-x64','RemoteFrame.Office.Windows') {
    if (@($items | Where-Object id -EQ $required).Count -ne 1) { throw "Missing or duplicate release package: $required" }
}
if (@($items | Where-Object id -Like 'RemoteFrame.Office.Runtime.win-x64.Part*').Count -eq 0) { throw 'Missing split runtime packages.' }
if (@($items.id | Sort-Object -Unique).Count -ne $items.Count) { throw 'Duplicate package identities.' }
foreach ($item in $items) {
    foreach ($dependency in $item.dependencies) {
        if ($dependency -notin $items.id) { throw "Missing release dependency: $dependency" }
    }
}
[ordered]@{version=$Version; commit=(& git rev-parse HEAD); packages=$items} | ConvertTo-Json -Depth 5 | Set-Content "$Directory/release.json"
Write-Output "Verified $($items.Count) packages; wrote release.json with sizes and SHA-256 hashes."
