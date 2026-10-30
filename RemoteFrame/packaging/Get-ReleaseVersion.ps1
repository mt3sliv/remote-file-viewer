param([string]$Version)
$ErrorActionPreference = 'Stop'
if (-not $Version) {
    [xml]$settings = Get-Content "$PSScriptRoot/../Directory.Build.props" -Raw
    $Version = [string]$settings.Project.PropertyGroup.Version.'#text'
}
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Invalid release version: $Version"
}
foreach ($identifier in ($Version -split '-',2 | Select-Object -Skip 1) -split '\.') {
    if ($identifier -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers cannot have leading zeroes.' }
}
Write-Output $Version
