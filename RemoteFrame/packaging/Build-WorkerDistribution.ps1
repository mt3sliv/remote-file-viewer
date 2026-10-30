param([string]$Version)
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
$root = Split-Path $PSScriptRoot -Parent
$packages = "$root/artifacts/packages"
function Run-DotNet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed." } }
# These local managed packages are used for verification, not uploaded by the worker job.
foreach ($name in 'RemoteFrame.Core','RemoteFrame.Rendering','RemoteFrame.AspNetCore') {
    Run-DotNet pack "$root/src/$name" -c Release -o $packages "-p:Version=$Version"
}
& "$PSScriptRoot/Build-WindowsWorker.ps1" -Version $Version
$aggregate = "$PSScriptRoot/RemoteFrame.Office.Windows/RemoteFrame.Office.Windows.csproj"
Run-DotNet restore $aggregate "-p:Version=$Version" "-p:OfficeRuntimePackageVersion=$Version" "-p:ManagedPackageVersion=$Version"
Run-DotNet pack $aggregate -c Release --no-build --no-restore -o $packages "-p:Version=$Version" "-p:OfficeRuntimePackageVersion=$Version" "-p:ManagedPackageVersion=$Version"
$consumer = Join-Path $root ('artifacts/worker-consumer/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $consumer | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><PackageReference Include="RemoteFrame.Office.Windows" Version="[$Version]" /></ItemGroup></Project>
"@ | Set-Content "$consumer/consumer.csproj"
@'
using RemoteFrame.AspNetCore;
var runtime = BundledOfficeRuntime.Load();
if (runtime.EnginePath != "" || !File.Exists(runtime.WorkerPath)) throw new Exception("Expected worker without bundled LibreOffice.");
BundledOfficeRuntime.Validate(Path.Combine(AppContext.BaseDirectory, "remote-frame", "win-x64"), "win-x64");
Console.WriteLine("PASS: Public Office.Windows package loads and validates its worker without LibreOffice.");
'@ | Set-Content "$consumer/Program.cs"
Run-DotNet run --project "$consumer/consumer.csproj" -c Release "-p:RestorePackagesPath=$consumer/cache"
$assets = Get-Content "$consumer/obj/project.assets.json" -Raw
if ($assets -match 'RemoteFrame.LibreOffice|RemoteFrame.Office.Runtime.win-x64') { throw 'Worker-only dependency graph included the engine.' }
Run-DotNet publish "$consumer/consumer.csproj" -c Release -o "$consumer/published" "-p:RestorePackagesPath=$consumer/cache"
Run-DotNet "$consumer/published/consumer.dll"
& "$PSScriptRoot/New-ReleaseManifest.ps1" -Version $Version -Group Worker
& "$PSScriptRoot/Publish-NuGet.ps1" -Version $Version -Group Worker -ValidateOnly
