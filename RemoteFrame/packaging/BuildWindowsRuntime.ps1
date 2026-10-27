param(
    [Parameter(Mandatory=$true)][string]$SourceBuildReceipt,
    [Parameter(Mandatory=$true)][string]$VcRedistDirectory,
    [Parameter(Mandatory=$true)][string]$MicrosoftNoticesPath,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+-source\.[0-9]+$')][string]$PackageVersion = '0.3.0-source.1'
)
$ErrorActionPreference = 'Stop'
$remoteFrameRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'libreoffice/SourceBuild.psm1') -Force
$sourceBuild = Assert-SourceBuildReceipt $SourceBuildReceipt
$LibreOfficeDirectory = $sourceBuild.OutputDirectory
# Versioned recipe directories prevent stale files from an older engine entering the package.
$payload = Join-Path $remoteFrameRoot "artifacts/native-source/$($sourceBuild.Recipe.Fingerprint.Substring(0,16))/win-x64"
$packages = Join-Path $remoteFrameRoot 'artifacts/packages'
$workerOutput = Join-Path $remoteFrameRoot 'artifacts/windows-worker'
foreach ($name in @('RemoteFrame.Office.Runtime.win-x64','RemoteFrame.Office.Windows')) {
    if (Test-Path -LiteralPath (Join-Path $packages "$name.$PackageVersion.nupkg")) {
        throw "Package $name.$PackageVersion already exists. Choose a new source package version rather than overwriting cached binaries."
    }
}
function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
foreach ($required in @("$LibreOfficeDirectory/program/soffice.exe", "$LibreOfficeDirectory/license.txt", "$VcRedistDirectory/vcruntime140.dll", $MicrosoftNoticesPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing build input: $required" }
}
New-Item -ItemType Directory -Force $packages, "$payload/office", "$payload/worker", "$payload/notices" | Out-Null
Invoke-DotNet publish (Join-Path $remoteFrameRoot 'src/RemoteFrame.Office.Worker/RemoteFrame.Office.Worker.csproj') -c Release -r win-x64 --self-contained true -o $workerOutput
& (Join-Path $PSScriptRoot 'native/BuildInputHook.cmd') $workerOutput
if ($LASTEXITCODE -ne 0) { throw 'Failed to build the native Office input helper' }
Get-ChildItem -LiteralPath $LibreOfficeDirectory -Force | Copy-Item -Destination "$payload/office" -Recurse -Force
Get-ChildItem -LiteralPath $workerOutput -Force | Where-Object Extension -NotIn '.obj', '.lib', '.exp' | Copy-Item -Destination "$payload/worker" -Recurse -Force
$runtimeConfig = Get-Content -LiteralPath "$workerOutput/RemoteFrame.Office.Worker.runtimeconfig.json" -Raw | ConvertFrom-Json
$dotnetVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$nugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$dotnetRuntime = Join-Path $nugetCache "microsoft.netcore.app.runtime.win-x64/$dotnetVersion"
Copy-Item -LiteralPath "$dotnetRuntime/LICENSE.TXT" -Destination "$payload/worker/LICENSE.txt" -Force
Copy-Item -LiteralPath "$dotnetRuntime/THIRD-PARTY-NOTICES.TXT" -Destination "$payload/worker/THIRD-PARTY-NOTICES.txt" -Force
Get-ChildItem -LiteralPath $VcRedistDirectory -Filter '*.dll' | Copy-Item -Destination "$payload/office/program" -Force
Copy-Item -LiteralPath "$LibreOfficeDirectory/license.txt" -Destination "$payload/notices/LibreOffice-license.txt" -Force
Copy-Item -LiteralPath "$LibreOfficeDirectory/NOTICE" -Destination "$payload/notices/LibreOffice-NOTICE" -Force
Copy-Item -LiteralPath $MicrosoftNoticesPath -Destination "$payload/notices/Microsoft-ThirdPartyNotices.txt" -Force
# Exercise the freshly staged source engine, not an installed LibreOffice or an old package.
$workerProbe = Join-Path $remoteFrameRoot 'tests/TestWindowsWorker.ps1'
& $workerProbe -Engine "$payload/office/program/soffice.exe" -Worker "$payload/worker/RemoteFrame.Office.Worker.exe" -EditAndSave
& $workerProbe -Engine "$payload/office/program/soffice.exe" -Worker "$payload/worker/RemoteFrame.Office.Worker.exe" -Sample TestLibreOfficeCalcDocument.ods -EditAndSave
$revision = (Select-String -LiteralPath "$LibreOfficeDirectory/program/version.ini" -Pattern '^buildid=(.+)$').Matches.Groups[1].Value
if (-not $revision) { throw 'The LibreOffice build must provide its pinned source revision' }
$version = (Get-Item -LiteralPath "$LibreOfficeDirectory/program/soffice.exe").VersionInfo.ProductVersion
$provenance = [ordered]@{
    method='source'; version=$sourceBuild.Receipt.version; upstreamRevision=$sourceBuild.Receipt.upstreamRevision
    sourceTree=$sourceBuild.Receipt.sourceTree; recipeFingerprint=$sourceBuild.Receipt.recipeFingerprint
    profileSha256=$sourceBuild.Receipt.profileSha256; patchesSha256=$sourceBuild.Receipt.patchesSha256
    buildSucceeded=$true; testsSucceeded=$true; completedUtc=$sourceBuild.Receipt.completedUtc
    workerSmokeChecks=@('Writer edit/save','Calc edit/save')
}
$provenance | ConvertTo-Json | Set-Content -LiteralPath "$payload/notices/RemoteFrame-source-build.json"
Copy-Item -LiteralPath $sourceBuild.Recipe.LockPath -Destination "$payload/notices/source.lock.json" -Force
Copy-Item -LiteralPath $sourceBuild.Recipe.Profile -Destination "$payload/notices/configure.args" -Force
New-Item -ItemType Directory -Force "$payload/notices/patches" | Out-Null
Copy-Item -LiteralPath $sourceBuild.Recipe.Series -Destination "$payload/notices/patches/series" -Force
foreach ($patch in $sourceBuild.Recipe.Patches) { Copy-Item -LiteralPath $patch.Path -Destination "$payload/notices/patches/$($patch.Name)" -Force }
Invoke-DotNet run --project (Join-Path $remoteFrameRoot 'tools/RemoteFrame.RuntimeCheck') --configuration Release -- '--create' $payload 'win-x64' $version $revision 'office/program/soffice.exe' 'worker/RemoteFrame.Office.Worker.exe' 'notices/LibreOffice-license.txt' 'notices/LibreOffice-NOTICE' 'notices/Microsoft-ThirdPartyNotices.txt' 'worker/LICENSE.txt' 'worker/THIRD-PARTY-NOTICES.txt'
foreach ($project in @('RemoteFrame.Core', 'RemoteFrame.Rendering', 'RemoteFrame.AspNetCore')) {
    Invoke-DotNet pack (Join-Path $remoteFrameRoot "src/$project/$project.csproj") -c Release -o $packages
}
Invoke-DotNet pack (Join-Path $PSScriptRoot 'RemoteFrame.Office.Runtime.csproj') -c Release -o $packages '-p:OfficeRuntimeIdentifier=win-x64' "-p:OfficePayloadRoot=$payload" "-p:Version=$PackageVersion"
$aggregate = Join-Path $PSScriptRoot 'RemoteFrame.Office.Windows/RemoteFrame.Office.Windows.csproj'
Invoke-DotNet restore $aggregate "-p:OfficeRuntimePackageVersion=$PackageVersion" "-p:Version=$PackageVersion"
Invoke-DotNet pack $aggregate -c Release --no-build -o $packages "-p:OfficeRuntimePackageVersion=$PackageVersion" "-p:Version=$PackageVersion"
Write-Output "Local Windows Office preview packages are ready in $packages"
