param(
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = "$PSScriptRoot/../artifacts/packages",
    [ValidateRange(1,180000000)][long]$PartBudgetBytes = 180000000
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
$Payload = [IO.Path]::GetFullPath($Payload)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& dotnet run --project "$root/tools/RemoteFrame.RuntimeCheck" -c Release -- $Payload win-x64
if ($LASTEXITCODE -ne 0) { throw 'Complete input payload failed validation.' }
$original = Get-Content "$Payload/runtime.json" -Raw | ConvertFrom-Json -AsHashtable
foreach ($id in 'RemoteFrame.Office.Worker.Runtime.win-x64','RemoteFrame.LibreOffice.Runtime.win-x64','RemoteFrame.Office.Runtime.win-x64') {
    if (@(Get-ChildItem $OutputDirectory -Filter "$id*.$Version.nupkg" -ErrorAction SilentlyContinue).Count) { throw "Packages already exist: $id/$Version. Use a fresh version." }
}
$work = Join-Path $root ('artifacts/office-components/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
foreach ($component in 'worker','libreoffice') {
    $stage = "$work/$component"
    $files = [ordered]@{}
    foreach ($relative in $original.files.Keys | Sort-Object) {
        $isWorker = $relative.StartsWith('worker/')
        if ($isWorker -ne ($component -eq 'worker')) { continue }
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Payload $relative) -Destination $destination
        $files[$relative] = $original.files[$relative]
    }
    $manifest = [ordered]@{
        schemaVersion=2; component=$component; runtimeIdentifier='win-x64'
        version=$Version; sourceRevision=$(if ($component -eq 'worker') { & git -C $root rev-parse HEAD } else { $original.sourceRevision })
        engine=$(if ($component -eq 'libreoffice') { $original.engine } else { $null })
        worker=$(if ($component -eq 'worker') { $original.worker } else { $null })
        licenses=@($original.licenses | Where-Object { $_.StartsWith('worker/') -eq ($component -eq 'worker') })
        files=$files
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content "$stage/runtime.json"
    $id = if ($component -eq 'worker') { 'RemoteFrame.Office.Worker.Runtime.win-x64' } else { 'RemoteFrame.LibreOffice.Runtime.win-x64' }
    & "$PSScriptRoot/Pack-SplitRuntime.ps1" -Payload $stage -Version $Version -OutputDirectory $OutputDirectory `
        -PackageId $id -DeploymentSubdirectory "components/$component" -PartBudgetBytes $PartBudgetBytes `
        -Readme "$PSScriptRoot/$($component.ToUpperInvariant())-RUNTIME-PACKAGE.md"
}
# Retain the old runtime ID as a compatibility coordinator; it still supplies both components.
Copy-Item "$PSScriptRoot/RUNTIME-PACKAGE.md" "$work/README.md"
[IO.File]::WriteAllText("$work/_._",'')
@"
<configuration><packageSources><clear/><add key="RemoteFrameLocal" value="$([Security.SecurityElement]::Escape($OutputDirectory))"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
"@ | Set-Content "$work/NuGet.Config"
@"
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultItems>false</EnableDefaultItems><IncludeBuildOutput>false</IncludeBuildOutput><PackageId>RemoteFrame.Office.Runtime.win-x64</PackageId><PackageReadmeFile>README.md</PackageReadmeFile><Description>Compatibility coordinator for the separate Windows worker and LibreOffice runtime packages.</Description></PropertyGroup>
 <ItemGroup><None Include="README.md" Pack="true" PackagePath="/" /><None Include="_._" Pack="true" PackagePath="lib/net10.0/" />
 <PackageReference Include="RemoteFrame.Office.Worker.Runtime.win-x64" Version="[$Version]" />
 <PackageReference Include="RemoteFrame.LibreOffice.Runtime.win-x64" Version="[$Version]" /></ItemGroup>
</Project>
"@ | Set-Content "$work/compat.csproj"
& dotnet pack "$work/compat.csproj" -c Release -o $OutputDirectory "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Compatibility coordinator packaging failed.' }
