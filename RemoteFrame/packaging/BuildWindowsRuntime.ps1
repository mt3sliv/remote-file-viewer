param(
    [string]$SourceBuildReceipt,
    [string]$BinaryBuildReceipt,
    [string]$StagedPayload,
    [ValidateSet('Binary','Source')][string]$EngineMode = $(if ($SourceBuildReceipt) { 'Source' } else { 'Binary' }),
    [Parameter(Mandatory=$true)][string]$VcRedistDirectory,
    [Parameter(Mandatory=$true)][string]$MicrosoftNoticesPath,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+-(source|preview)\.[0-9]+$')][string]$PackageVersion = $(if ($EngineMode -eq 'Source') { '0.3.0-source.2' } else { '0.3.0-preview.7' }),
    [string]$ManagedPackageVersion = '0.3.0-preview.7'
)
$ErrorActionPreference = 'Stop'
$remoteFrameRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'libreoffice/SourceBuild.psm1') -Force
if ($EngineMode -eq 'Source') {
    Write-Host 'Validating the successful source-build receipt...'
    if (-not $SourceBuildReceipt -or $BinaryBuildReceipt -or $PackageVersion -notmatch '-source\.') { throw 'Source mode requires only a successful SourceBuildReceipt and a source package version.' }
    $sourceBuild = Assert-SourceBuildReceipt $SourceBuildReceipt
    $LibreOfficeDirectory = $sourceBuild.OutputDirectory
    $identity = $sourceBuild.Recipe.Fingerprint.Substring(0,16)
} else {
    Write-Host 'Validating the pinned binary-build receipt...'
    if ($SourceBuildReceipt -or -not $BinaryBuildReceipt -or $PackageVersion -notmatch '-preview\.') { throw 'Binary mode requires only a verified BinaryBuildReceipt and a preview package version.' }
    Import-Module (Join-Path $PSScriptRoot 'libreoffice/BinaryBuild.psm1') -Force
    $binaryBuild = Assert-BinaryBuildReceipt $BinaryBuildReceipt
    $LibreOfficeDirectory = $binaryBuild.OutputDirectory
    $identity = $binaryBuild.Lock.Value.sha256.Substring(0,16)
}
# Fresh payloads prevent stale files from a previous/failed attempt entering either mode.
$payload = Join-Path $remoteFrameRoot "artifacts/native-$($EngineMode.ToLowerInvariant())/$identity/$([Guid]::NewGuid().ToString('N'))/win-x64"
if ($StagedPayload) {
    $payload = [IO.Path]::GetFullPath($StagedPayload)
    $allowed = [IO.Path]::GetFullPath((Join-Path $remoteFrameRoot "artifacts/native-$($EngineMode.ToLowerInvariant())/$identity")) + [IO.Path]::DirectorySeparatorChar
    if (-not $payload.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($payload) -ne 'win-x64') {
        throw 'A resumed payload must be inside the selected engine identity directory and end in win-x64.'
    }
}
$packages = Join-Path $remoteFrameRoot 'artifacts/packages'
$workerOutput = Join-Path $remoteFrameRoot 'artifacts/windows-worker'
foreach ($name in @('RemoteFrame.Office.Runtime.win-x64','RemoteFrame.Office.Windows')) {
    if (Test-Path -LiteralPath (Join-Path $packages "$name.$PackageVersion.nupkg")) {
        throw "Package $name.$PackageVersion already exists. Choose a new package version rather than overwriting cached binaries."
    }
}
function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
function Assert-StagedEngine {
    $expected = if ($EngineMode -eq 'Binary') { $binaryBuild.Receipt.outputFiles.Clone() } else { $sourceBuild.Receipt.outputFiles.Clone() }
    foreach ($crt in Get-ChildItem -LiteralPath $VcRedistDirectory -Filter '*.dll') { $expected["program/$($crt.Name)"] = Get-FileDigest $crt.FullName }
    $actual = Get-EngineInventory "$payload/office"
    if ($actual.Count -ne $expected.Count) { throw 'Staged engine inventory differs from the selected engine and CRT.' }
    foreach ($name in $expected.Keys) { if ($actual[$name] -ne $expected[$name]) { throw "Staged engine changed: $name" } }
}
foreach ($required in @("$LibreOfficeDirectory/program/soffice.exe", "$LibreOfficeDirectory/license.txt", "$VcRedistDirectory/vcruntime140.dll", $MicrosoftNoticesPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing build input: $required" }
}
New-Item -ItemType Directory -Force $packages, "$payload/office", "$payload/worker", "$payload/notices" | Out-Null
Write-Host 'Building the RemoteFrame worker and native input helper...'
Invoke-DotNet publish (Join-Path $remoteFrameRoot 'src/RemoteFrame.Office.Worker/RemoteFrame.Office.Worker.csproj') -c Release -r win-x64 --self-contained true -o $workerOutput
& (Join-Path $PSScriptRoot 'native/BuildInputHook.cmd') $workerOutput
if ($LASTEXITCODE -ne 0) { throw 'Failed to build the native Office input helper' }
Write-Host 'Staging the complete engine, worker, runtime dependencies and notices...'
if ($StagedPayload) {
    Assert-StagedEngine
    Write-Host 'Verified previously staged engine; reusing it without another full copy.'
} else {
    Get-ChildItem -LiteralPath $LibreOfficeDirectory -Force | Copy-Item -Destination "$payload/office" -Recurse -Force
}
Get-ChildItem -LiteralPath $workerOutput -Force | Where-Object Extension -NotIn '.obj', '.lib', '.exp' | Copy-Item -Destination "$payload/worker" -Recurse -Force
# Resuming may not retain obsolete worker binaries from an earlier build.
$workerExpected = @(Get-ChildItem -LiteralPath $workerOutput -File -Force | Where-Object Extension -NotIn '.obj', '.lib', '.exp' | Select-Object -ExpandProperty Name)
$workerActual = @(Get-ChildItem -LiteralPath "$payload/worker" -File -Force | Where-Object Name -NotIn 'LICENSE.txt','THIRD-PARTY-NOTICES.txt' | Select-Object -ExpandProperty Name)
if (Compare-Object ($workerExpected | Sort-Object) ($workerActual | Sort-Object)) { throw 'Staged worker has missing or obsolete files; use a fresh payload.' }
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
# Always test the staged engine, regardless of installed-application preference.
$workerProbe = Join-Path $remoteFrameRoot 'tests/TestWindowsWorker.ps1'
Write-Host 'Testing bundled Writer and Calc capture, resize, editing and save...'
& $workerProbe -Engine "$payload/office/program/soffice.exe" -Worker "$payload/worker/RemoteFrame.Office.Worker.exe" -EditAndSave
& $workerProbe -Engine "$payload/office/program/soffice.exe" -Worker "$payload/worker/RemoteFrame.Office.Worker.exe" -Sample TestLibreOfficeCalcDocument.ods -EditAndSave
Write-Host 'Verifying smoke checks did not modify the pinned engine...'
Assert-StagedEngine
$revision = (Select-String -LiteralPath "$LibreOfficeDirectory/program/version.ini" -Pattern '^buildid=(.+)$').Matches.Groups[1].Value
if (-not $revision) { throw 'The LibreOffice build must provide its pinned source revision' }
$version = (Get-Item -LiteralPath "$LibreOfficeDirectory/program/soffice.exe").VersionInfo.ProductVersion
if ($EngineMode -eq 'Source') {
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
} else {
    $provenance = [ordered]@{
        method='upstream-binary'; version=$binaryBuild.Lock.Value.version; upstreamRevision=$revision
        archiveUrl=$binaryBuild.Lock.Value.url; archiveSha256=$binaryBuild.Lock.Value.sha256
        lockSha256=$binaryBuild.Lock.Hash; upstreamSourceBuildTests='not run by RemoteFrame (official binary distribution)'
        workerSmokeChecks=@('Writer edit/save','Calc edit/save'); completedUtc=[DateTime]::UtcNow.ToString('O')
    }
    $provenance | ConvertTo-Json | Set-Content -LiteralPath "$payload/notices/RemoteFrame-binary-build.json"
    Copy-Item -LiteralPath $binaryBuild.Lock.Path -Destination "$payload/notices/binary.lock.json"
}
Invoke-DotNet run --project (Join-Path $remoteFrameRoot 'tools/RemoteFrame.RuntimeCheck') --configuration Release -- '--create' $payload 'win-x64' $version $revision 'office/program/soffice.exe' 'worker/RemoteFrame.Office.Worker.exe' 'notices/LibreOffice-license.txt' 'notices/LibreOffice-NOTICE' 'notices/Microsoft-ThirdPartyNotices.txt' 'worker/LICENSE.txt' 'worker/THIRD-PARTY-NOTICES.txt'
Write-Host 'Packing managed libraries, bundled native runtime and aggregate NuGets...'
foreach ($project in @('RemoteFrame.Core', 'RemoteFrame.Rendering', 'RemoteFrame.AspNetCore')) {
    Invoke-DotNet pack (Join-Path $remoteFrameRoot "src/$project/$project.csproj") -c Release -o $packages "-p:Version=$ManagedPackageVersion"
}
Invoke-DotNet pack (Join-Path $PSScriptRoot 'RemoteFrame.Office.Runtime.csproj') -c Release -o $packages '-p:OfficeRuntimeIdentifier=win-x64' "-p:OfficePayloadRoot=$payload" "-p:Version=$PackageVersion"
$aggregate = Join-Path $PSScriptRoot 'RemoteFrame.Office.Windows/RemoteFrame.Office.Windows.csproj'
Invoke-DotNet restore $aggregate "-p:OfficeRuntimePackageVersion=$PackageVersion" "-p:ManagedPackageVersion=$ManagedPackageVersion" "-p:Version=$PackageVersion"
Invoke-DotNet pack $aggregate -c Release --no-build -o $packages "-p:OfficeRuntimePackageVersion=$PackageVersion" "-p:ManagedPackageVersion=$ManagedPackageVersion" "-p:Version=$PackageVersion"
Write-Output "Local Windows Office preview packages are ready in $packages"
