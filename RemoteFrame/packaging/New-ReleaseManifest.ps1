param([Parameter(Mandatory)][string]$Version,[string]$Directory = "$PSScriptRoot/../artifacts/packages",
    [ValidateSet('All','Managed','Worker','LibreOffice','Integration')][string]$Group = 'All')
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
& "$PSScriptRoot/../tests/TestPackageReadmes.ps1" -Directory $Directory -Version $Version
$policy = & "$PSScriptRoot/Get-ReleasePackagePolicy.ps1" -Group $Group
$items = @(foreach ($file in Get-ChildItem $Directory -Filter "*.$Version.nupkg") {
    $fileId = $file.Name.Substring(0,$file.Name.Length - ".$Version.nupkg".Length)
    if ($Group -ne 'All' -and $fileId -notin $policy.Required -and -not @($policy.PartPrefixes | Where-Object { $fileId -match ('^' + [regex]::Escape($_) + '\.Part[0-9]{3}$') }).Count) { continue }
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
        $dependencyVersions = @{}
        foreach ($node in $spec.SelectNodes("//*[local-name()='dependency']") | Where-Object { $_.id -like 'RemoteFrame.*' }) { $dependencyVersions[[string]$node.id] = [string]$node.version }
        [ordered]@{ id=$id; file=$file.Name; sha256=(Get-FileHash $file.FullName).Hash; bytes=$file.Length; dependencies=$dependencies; dependencyVersions=$dependencyVersions }
    } finally { $zip.Dispose() }
})
foreach ($required in $policy.Required) {
    if (@($items | Where-Object id -EQ $required).Count -ne 1) { throw "Missing or duplicate release package: $required" }
}
foreach ($component in $policy.PartPrefixes) {
    if (@($items | Where-Object id -Like "$component.Part*").Count -eq 0) { throw "Missing split runtime packages: $component" }
}
if (@($items.id | Sort-Object -Unique).Count -ne $items.Count) { throw 'Duplicate package identities.' }
foreach ($item in $items) {
    foreach ($dependency in $item.dependencies) {
        if ($dependency -notin $items.id -and $dependency -notin $policy.ExternalDependencies) { throw "Missing release dependency: $dependency" }
        if ($item.dependencyVersions[$dependency] -notin @($Version,"[$Version]","[$Version, )","[$Version,)")) { throw "Dependency must use this release version: $dependency" }
    }
}
$name = if ($Group -eq 'All') { 'release.json' } else { "release-$Group.json" }
[ordered]@{version=$Version; group=$Group; commit=(& git rev-parse HEAD); packages=$items} | ConvertTo-Json -Depth 6 | Set-Content "$Directory/$name"
Write-Output "Verified $($items.Count) $Group packages; wrote $name with sizes and SHA-256 hashes."
