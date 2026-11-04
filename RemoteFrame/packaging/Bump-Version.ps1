[CmdletBinding(SupportsShouldProcess)]
param([string]$Version)
$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $PSScriptRoot '../Directory.Build.props'
$text = [IO.File]::ReadAllText($settingsPath)
$current = & "$PSScriptRoot/Get-ReleaseVersion.ps1"
if (-not $Version) {
    if ($current -match '^(?<prefix>\d+\.\d+\.\d+-.*\.)(?<number>\d+)$') {
        $Version = $Matches.prefix + ([System.Numerics.BigInteger]::Parse($Matches.number) + 1).ToString()
    } elseif ($current -match '^(?<prefix>\d+\.\d+\.)(?<number>\d+)$') {
        $Version = $Matches.prefix + ([System.Numerics.BigInteger]::Parse($Matches.number) + 1).ToString()
    } else {
        throw "Cannot increment '$current' automatically. Supply -Version with the desired release version."
    }
}
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
if ($Version -eq $current) { throw "Version is already $current." }
# Preserve the existing formatting and conditional MSBuild property.
$pattern = '(<Version\b[^>]*>)' + [regex]::Escape($current) + '(</Version>)'
$regex = [regex]::new($pattern)
if ($regex.Matches($text).Count -ne 1) { throw 'Expected exactly one default Version property.' }
$updated = $regex.Replace($text, [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $match.Groups[1].Value + $Version + $match.Groups[2].Value })
if ($PSCmdlet.ShouldProcess($settingsPath, "Bump package version from $current to $Version")) {
    [IO.File]::WriteAllText($settingsPath, $updated)
    Write-Output $Version
}
