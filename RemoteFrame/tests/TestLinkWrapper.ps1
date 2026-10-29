$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $root 'artifacts/link-wrapper-probe'
New-Item -ItemType Directory -Force $probe | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$version = Get-ChildItem -LiteralPath "$vs/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
$bin = "$vs/VC/Tools/MSVC/$version/bin/Hostx64/x64"
$env:REMOTEFRAME_REAL_LINK = "$bin/link.exe"
$wrapper = Join-Path $root 'artifacts/build-tools/link-wrapper/RemoteFrame.LinkWrapper.exe'
if (-not (Test-Path -LiteralPath $wrapper)) { throw 'Publish tools/RemoteFrame.LinkWrapper to artifacts/build-tools/link-wrapper first.' }
function Run-Tool([string]$Executable, [string[]]$ToolArguments, [string]$Log) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $ToolArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [IO.File]::WriteAllText($Log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
    $code = $process.ExitCode; $process.Dispose(); return $code
}
$source = Join-Path $probe 'probe.c'
$object = Join-Path $probe 'object with spaces.obj'
[IO.File]::WriteAllText($source, 'int remote_frame_link_probe(void) { return 1; }')
if ((Run-Tool "$bin/cl.exe" @('/nologo','/c',$source,"/Fo$object") "$probe/compiler.log") -ne 0) { throw 'Could not compile probe object' }
$argument = '"' + $object + '"'
$count = [int][Math]::Ceiling(150000 / ($argument.Length + 1))
$long = (($argument + ' ') * $count)
$rsp = Join-Path $probe 'long.rsp'
[IO.File]::WriteAllText($rsp, $long)
$baseline = Run-Tool $env:REMOTEFRAME_REAL_LINK @('/dump','/headers',"@$rsp") "$probe/baseline.log"
if ($baseline -eq 0 -or (Get-Content "$probe/baseline.log" -Raw) -notmatch 'LNK1170') { throw 'Probe did not reproduce the original linker line limit' }
Write-Output 'PASS: Probe reproduces MSVC LNK1170 with a long response-file line.'
if ((Run-Tool $wrapper @('/dump','/headers',"@$rsp") "$probe/wrapped.log") -ne 0) { throw 'Wrapped linker did not accept the long response file' }
$lines = [IO.File]::ReadAllLines($rsp) | Where-Object { $_.Length }
if ($lines.Count -ne $count -or @($lines | Where-Object { $_ -ne $argument }).Count) { throw 'Quoted response-file arguments changed' }
Write-Output 'PASS: Long lines are split without changing quoted object paths.'
[IO.File]::WriteAllText($rsp, $argument)
$before = (Get-FileHash -LiteralPath $rsp).Hash
if ((Run-Tool $wrapper @('/dump','/headers',"@$rsp") "$probe/short.log") -ne 0 -or (Get-FileHash -LiteralPath $rsp).Hash -ne $before) { throw 'Short response file was changed or failed' }
Write-Output 'PASS: Short response files remain unchanged.'
if ((Run-Tool $wrapper @('/dump','does-not-exist.obj') "$probe/error.log") -eq 0) { throw 'Linker failure was swallowed' }
Write-Output 'PASS: Real linker failures propagate.'
