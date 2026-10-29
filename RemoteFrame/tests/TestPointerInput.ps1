$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$helper = Join-Path $root 'artifacts/pointer-input-helper'
New-Item -ItemType Directory -Force $helper | Out-Null
& "$root/packaging/native/BuildInputHook.cmd" $helper
if ($LASTEXITCODE -ne 0) { throw 'Failed to rebuild the native pointer input helper.' }
& "$PSScriptRoot/TestExcelCursor.cmd" $helper
if ($LASTEXITCODE -ne 0) { throw 'Excel cursor import regressions failed.' }
& dotnet run --project "$PSScriptRoot/RemoteFrame.PointerTests"
if ($LASTEXITCODE -ne 0) { throw 'Native pointer regressions failed.' }
