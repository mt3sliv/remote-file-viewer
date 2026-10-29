$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $root 'artifacts/python-uwp-compat-probe'
New-Item -ItemType Directory -Force $probe | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$msbuild = "$vs/MSBuild/Current/Bin/amd64/MSBuild.exe"
$hook = Join-Path $root 'packaging/native/PythonUwpCompat.props'
@'
#include <experimental/coroutine>
int probe_legacy_coroutine(void) { return 0; }
'@ | Set-Content -Encoding ascii "$probe/probe.cpp"
$project = @'
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations"><ProjectConfiguration Include="Release|x64"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration></ItemGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Label="Configuration"><ConfigurationType>StaticLibrary</ConfigurationType></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
  <PropertyGroup><OutDir>$(ProjectDir)out\$(MSBuildProjectName)\</OutDir><IntDir>$(ProjectDir)obj\$(MSBuildProjectName)\</IntDir></PropertyGroup>
  <ItemDefinitionGroup><ClCompile><AdditionalOptions>/await /std:c++17 %(AdditionalOptions)</AdditionalOptions></ClCompile></ItemDefinitionGroup>
  <ItemGroup><ClCompile Include="probe.cpp" /></ItemGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
</Project>
'@
foreach ($name in @('python_uwp','pythonw_uwp','unrelated')) { [IO.File]::WriteAllText("$probe/$name.vcxproj",$project) }
function Build-Probe([string]$Name, [bool]$UseHook) {
    $start = [Diagnostics.ProcessStartInfo]::new($msbuild)
    $start.UseShellExecute=$false; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($arg in @("$probe/$Name.vcxproj",'/t:Rebuild','/p:Configuration=Release','/p:Platform=x64','/p:PlatformToolset=v145','/p:VisualStudioVersion=18.0','/ToolsVersion:Current','/nologo','/verbosity:minimal')) { $start.ArgumentList.Add($arg) }
    if ($UseHook) { $start.Environment['ForceImportBeforeCppTargets']=$hook } else { $start.Environment.Remove('ForceImportBeforeCppTargets') | Out-Null }
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [IO.File]::WriteAllText("$probe/$Name-$UseHook.log",$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult())
    $code=$process.ExitCode; $process.Dispose(); return $code
}
if ((Build-Probe python_uwp $false) -eq 0 -or (Get-Content "$probe/python_uwp-False.log" -Raw) -notmatch 'STL1011') { throw 'Baseline did not reproduce the deprecation error' }
Write-Output 'PASS: Native probe reproduces MSVC STL1011.'
foreach ($name in @('python_uwp','pythonw_uwp')) { if ((Build-Probe $name $true) -ne 0) { throw "Compatibility hook failed for $name" } }
Write-Output 'PASS: Targeted compatibility hook enables both Python UWP launcher projects.'
if ((Build-Probe unrelated $true) -eq 0 -or (Get-Content "$probe/unrelated-True.log" -Raw) -notmatch 'STL1011') { throw 'Compatibility flag escaped its project scope' }
Write-Output 'PASS: Unrelated native projects retain the compiler deprecation guard.'
