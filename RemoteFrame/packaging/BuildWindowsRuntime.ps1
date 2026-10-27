param(
    [Parameter(Mandatory=$true)][string]$LibreOfficeDirectory,
    [Parameter(Mandatory=$true)][string]$VcRedistDirectory,
    [Parameter(Mandatory=$true)][string]$MicrosoftNoticesPath,
    [switch]$UseStagedOffice
)
$ErrorActionPreference = 'Stop'
$remoteFrameRoot = Split-Path $PSScriptRoot -Parent
$payload = Join-Path $remoteFrameRoot 'artifacts/native/win-x64'
$packages = Join-Path $remoteFrameRoot 'artifacts/packages'
$workerOutput = Join-Path $remoteFrameRoot 'artifacts/windows-worker'
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
if (-not $UseStagedOffice) {
    Get-ChildItem -LiteralPath $LibreOfficeDirectory -Force | Copy-Item -Destination "$payload/office" -Recurse -Force
} elseif ((Get-FileHash -LiteralPath "$payload/office/program/soffice.exe").Hash -ne (Get-FileHash -LiteralPath "$LibreOfficeDirectory/program/soffice.exe").Hash) {
    throw 'Staged Office does not match the selected build input'
}
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
$revision = (Select-String -LiteralPath "$LibreOfficeDirectory/program/version.ini" -Pattern '^buildid=(.+)$').Matches.Groups[1].Value
if (-not $revision) { throw 'The LibreOffice build must provide its pinned source revision' }
$version = (Get-Item -LiteralPath "$LibreOfficeDirectory/program/soffice.exe").VersionInfo.ProductVersion
Invoke-DotNet run --project (Join-Path $remoteFrameRoot 'tools/RemoteFrame.RuntimeCheck') --configuration Release -- '--create' $payload 'win-x64' $version $revision 'office/program/soffice.exe' 'worker/RemoteFrame.Office.Worker.exe' 'notices/LibreOffice-license.txt' 'notices/LibreOffice-NOTICE' 'notices/Microsoft-ThirdPartyNotices.txt' 'worker/LICENSE.txt' 'worker/THIRD-PARTY-NOTICES.txt'
foreach ($project in @('RemoteFrame.Core', 'RemoteFrame.Rendering', 'RemoteFrame.AspNetCore')) {
    Invoke-DotNet pack (Join-Path $remoteFrameRoot "src/$project/$project.csproj") -c Release -o $packages
}
Invoke-DotNet pack (Join-Path $PSScriptRoot 'RemoteFrame.Office.Runtime.csproj') -c Release -o $packages '-p:OfficeRuntimeIdentifier=win-x64' "-p:OfficePayloadRoot=$payload"
$aggregate = Join-Path $PSScriptRoot 'RemoteFrame.Office.Windows/RemoteFrame.Office.Windows.csproj'
Invoke-DotNet restore $aggregate
Invoke-DotNet pack $aggregate -c Release --no-build -o $packages
Write-Output "Local Windows Office preview packages are ready in $packages"
