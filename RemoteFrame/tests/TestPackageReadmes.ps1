param([string]$Directory = "$PSScriptRoot/../artifacts/packages",[Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$count = 0
foreach ($file in Get-ChildItem $Directory -Filter "*.$Version.nupkg") {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $specEntry = @($zip.Entries | Where-Object FullName -Like '*.nuspec')[0]
        $reader = [IO.StreamReader]::new($specEntry.Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $entry = $zip.GetEntry([string]$spec.package.metadata.readme)
        if (-not $entry) { throw "Missing declared README: $($file.Name)" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($text -match 'No packages have been published externally|not published NuGet.org releases|0\.3\.0-source\.1') { throw "Stale publication claim in $($file.Name)" }
        foreach ($link in [regex]::Matches($text,'\]\(([^)]+)\)')) {
            if ($link.Groups[1].Value -notmatch '^(https://|#)') { throw "Package README has a repository-relative link: $($file.Name)" }
        }
        $id = [string]$spec.package.metadata.id
        if ($id -eq 'RemoteFrame.Core' -and -not $text.StartsWith('# RemoteFrame.Core')) { throw 'Core has the wrong package guide.' }
        if ($id -eq 'RemoteFrame.Rendering' -and -not $text.StartsWith('# RemoteFrame.Rendering')) { throw 'Rendering has the wrong package guide.' }
        if ($id -eq 'RemoteFrame.Office.Windows' -and -not $text.StartsWith('# RemoteFrame.Office.Windows')) { throw 'Aggregate has the wrong package guide.' }
        $count++
    } finally { $zip.Dispose() }
}
if ($count -eq 0) { throw 'No packages were checked.' }
Write-Output "PASS: $count packed READMEs exist, identify their package, avoid stale publication claims and use NuGet-safe links."
