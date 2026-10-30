param(
    [ValidateSet('Binary','Source')][string]$OfficeEngine = 'Binary',
    [string]$SourceBuildReceipt,
    [string]$StagedPayload,
    [string]$VcRedistDirectory,
    [string]$MicrosoftNoticesPath,
    [string]$PackageVersion,
    [string]$ManagedPackageVersion,
    [ValidateRange(1,64)][int]$Jobs = 8
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PackageVersion) { $PackageVersion = if ($OfficeEngine -eq 'Source') { '0.3.0-source.2' } else { & "$PSScriptRoot/Get-ReleaseVersion.ps1" } }
if (-not $ManagedPackageVersion) { $ManagedPackageVersion = $PackageVersion }
if (-not $IsWindows) { throw 'Windows distributions currently require Windows x64.' }
if ($OfficeEngine -eq 'Binary' -and $SourceBuildReceipt) { throw 'A source receipt cannot be used in Binary mode.' }
$root = Split-Path $PSScriptRoot -Parent
if (-not $VcRedistDirectory -or -not $MicrosoftNoticesPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Visual Studio C++ tools are required to build the RemoteFrame input helper.' }
    if (-not $VcRedistDirectory) {
        $redist = Get-ChildItem -LiteralPath "$vs/VC/Redist/MSVC" -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+(\.\d+)?$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (-not $redist) { throw 'No versioned Visual C++ redistributable directory was found.' }
        $crt = @(Get-ChildItem -LiteralPath "$($redist.FullName)/x64" -Directory -Filter 'Microsoft.VC*.CRT')
        if ($crt.Count -ne 1) { throw 'Cannot identify the x64 Visual C++ runtime directory.' }
        $VcRedistDirectory = $crt[0].FullName
    }
    if (-not $MicrosoftNoticesPath) { $MicrosoftNoticesPath = "$vs/Licenses/1033/ThirdPartyNotices.txt" }
}
$arguments = @{
    EngineMode=$OfficeEngine; VcRedistDirectory=$VcRedistDirectory
    MicrosoftNoticesPath=$MicrosoftNoticesPath; ManagedPackageVersion=$ManagedPackageVersion
}
if ($PackageVersion) { $arguments.PackageVersion=$PackageVersion }
if ($StagedPayload) { $arguments.StagedPayload=$StagedPayload }
if ($OfficeEngine -eq 'Binary') {
    Write-Host 'Using verified upstream LibreOffice binaries; no LibreOffice compiler/Cygwin build is run.'
    $arguments.BinaryBuildReceipt = & "$PSScriptRoot/libreoffice/Prepare-WindowsBinary.ps1"
} else {
    if (-not $SourceBuildReceipt) {
        . "$PSScriptRoot/libreoffice/Set-WindowsBuildEnvironment.ps1"
        & "$PSScriptRoot/libreoffice/Build-LibreOffice.ps1" -Phase Build -BuildRoot "$root/artifacts/libreoffice-build" -Jobs $Jobs
        Import-Module "$PSScriptRoot/libreoffice/SourceBuild.psm1" -Force
        $recipe = Get-SourceRecipe
        $SourceBuildReceipt = "$root/artifacts/libreoffice-build/$($recipe.Fingerprint.Substring(0,16))/source-build.json"
    }
    $arguments.SourceBuildReceipt=$SourceBuildReceipt
}
& "$PSScriptRoot/BuildWindowsRuntime.ps1" @arguments
