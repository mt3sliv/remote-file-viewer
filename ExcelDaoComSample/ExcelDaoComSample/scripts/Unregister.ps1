param(
    [ValidateSet('x64','x86')][string]$OfficeBitness = 'x64',
    [string]$Dll = (Join-Path $PSScriptRoot '..\src\ExcelDaoBridge\bin\Release\net48\ExcelDaoBridge.dll')
)
$ErrorActionPreference = 'Stop'
$regasmRoot = if ($OfficeBitness -eq 'x64') { 'Framework64' } else { 'Framework' }
$regasm = Join-Path $env:WINDIR "Microsoft.NET\$regasmRoot\v4.0.30319\RegAsm.exe"
$dllPath = (Resolve-Path $Dll).Path
& $regasm $dllPath /unregister /nologo
if ($LASTEXITCODE -ne 0) { throw "RegAsm failed with $LASTEXITCODE" }
