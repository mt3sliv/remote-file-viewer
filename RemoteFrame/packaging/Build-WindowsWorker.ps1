param(
    [string]$Version,
    [string]$OutputDirectory = "$PSScriptRoot/../artifacts/packages"
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The native worker must be built on Windows with the C++ tools installed.' }
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root ('artifacts/standalone-worker/' + [Guid]::NewGuid().ToString('N'))
$worker = "$stage/worker"
& dotnet publish "$root/src/RemoteFrame.Office.Worker" -c Release -r win-x64 --self-contained true -o $worker "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
& "$PSScriptRoot/native/BuildInputHook.cmd" $worker
if ($LASTEXITCODE -ne 0) { throw 'Input helper build failed.' }
# Compiler/linker intermediates are not runtime assets.
foreach ($name in 'InputHook.obj','InputHook.lib','InputHook.exp','RemoteFrame.InputHook.exp') {
    $file = Join-Path $worker $name
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
$config = Get-Content "$worker/RemoteFrame.Office.Worker.runtimeconfig.json" -Raw | ConvertFrom-Json
$framework = ($config.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App').version
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
Copy-Item "$cache/microsoft.netcore.app.runtime.win-x64/$framework/LICENSE.TXT" "$worker/LICENSE.txt"
Copy-Item "$cache/microsoft.netcore.app.runtime.win-x64/$framework/THIRD-PARTY-NOTICES.TXT" "$worker/THIRD-PARTY-NOTICES.txt"
$files = [ordered]@{}
foreach ($file in Get-ChildItem $worker -Recurse -Force -File) {
    $relative = [IO.Path]::GetRelativePath($stage,$file.FullName).Replace('\','/')
    $files[$relative] = (Get-FileHash $file.FullName).Hash
}
[ordered]@{
    schemaVersion=2; component='worker'; runtimeIdentifier='win-x64'; version=$Version
    sourceRevision=(& git -C $root rev-parse HEAD); engine=$null
    worker='worker/RemoteFrame.Office.Worker.exe'
    licenses=@('worker/LICENSE.txt','worker/THIRD-PARTY-NOTICES.txt'); files=$files
} | ConvertTo-Json -Depth 5 | Set-Content "$stage/runtime.json"
& "$PSScriptRoot/Pack-SplitRuntime.ps1" -Payload $stage -Version $Version -OutputDirectory $OutputDirectory `
    -PackageId 'RemoteFrame.Office.Worker.Runtime.win-x64' -DeploymentSubdirectory 'components/worker' `
    -Readme "$PSScriptRoot/WORKER-RUNTIME-PACKAGE.md"
Write-Output "Built standalone worker without a LibreOffice download: $stage"
