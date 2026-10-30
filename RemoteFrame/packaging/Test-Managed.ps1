param([string]$Version)
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    function Run-DotNet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed." } }
    Run-DotNet build src/RemoteFrame.Server -c Release '-p:UseRemoteFramePackages=false' "-p:Version=$Version"
    Run-DotNet build src/RemoteFrame.Office.Worker -c Release "-p:Version=$Version"
    foreach ($test in 'RemoteFrame.ApplicationTests','RemoteFrame.RuntimeTests','RemoteFrame.StreamingTests') {
        Run-DotNet run --project "tests/$test" -c Release "-p:Version=$Version"
    }
    & node --test tests/RemoteFrame.StreamingTests/viewer.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser input tests failed.' }
    & ./tests/TestReleasePolicy.ps1
    New-Item -ItemType Directory -Force artifacts/packages | Out-Null
    foreach ($project in 'RemoteFrame.Core','RemoteFrame.Rendering','RemoteFrame.AspNetCore') {
        Run-DotNet pack "src/$project" -c Release -o artifacts/packages "-p:Version=$Version"
    }
    Run-DotNet run --project tests/RemoteFrame.PackageSmoke -c Release "-p:Version=$Version" "-p:RemoteFramePackageVersion=$Version"
} finally { Pop-Location }
