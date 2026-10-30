$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = Join-Path $root ('artifacts/split-package-test/' + [Guid]::NewGuid().ToString('N'))
$payload = "$test/payload"
$version = '0.0.0-split.g' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Force "$payload/office/program","$payload/worker","$payload/notices","$test/feed","$test/consumer" | Out-Null
foreach ($name in 'office/program/soffice.exe','worker/worker.exe','office/program/extensionless','office/program/.dir') {
    [IO.File]::WriteAllBytes("$payload/$name",[Text.Encoding]::UTF8.GetBytes(('TEST FIXTURE ' + $name).PadRight(3000,'x')))
}
Set-Content "$payload/notices/LICENSE.txt" 'TEST FIXTURE LICENSE'
Set-Content "$payload/notices/NOTICE.txt" 'TEST FIXTURE NOTICE'
& dotnet run --project "$root/tools/RemoteFrame.RuntimeCheck" -c Release -- --create $payload win-x64 fixture abc office/program/soffice.exe worker/worker.exe notices/LICENSE.txt notices/NOTICE.txt
if ($LASTEXITCODE -ne 0) { throw 'Fixture manifest creation failed.' }
& "$root/packaging/Pack-SplitRuntime.ps1" -Payload $payload -Version $version -OutputDirectory "$test/feed" -PartBudgetBytes 4096
$parts = @(Get-ChildItem "$test/feed" -Filter '*Part*.nupkg')
if ($parts.Count -lt 3) { throw 'Fixture did not exercise multiple dependency parts.' }
@"
<configuration><packageSources><clear/><add key="RemoteFrameLocal" value="$test/feed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
"@ | Set-Content "$test/consumer/NuGet.Config"
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="RemoteFrame.Office.Runtime.win-x64" Version="[$version]" /></ItemGroup></Project>
"@ | Set-Content "$test/consumer/consumer.csproj"
foreach ($operation in 'build','publish') {
    $output = "$test/$operation"
    & dotnet $operation "$test/consumer/consumer.csproj" -c Release -o $output "-p:RestorePackagesPath=$test/cache"
    if ($LASTEXITCODE -ne 0) { throw "Clean consumer $operation failed." }
    $copied = "$output/remote-frame/win-x64"
    foreach ($file in Get-ChildItem $payload -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($payload,$file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath "$copied/$relative").Hash) {
            throw "Split package $operation lost or changed $relative"
        }
    }
    & dotnet run --project "$root/tools/RemoteFrame.RuntimeCheck" -c Release -- $copied win-x64
    if ($LASTEXITCODE -ne 0) { throw "Reassembled runtime failed validation after $operation." }
}
Write-Output 'PASS: A clean consumer restores all split dependencies and preserves every runtime byte during build and publish, including extensionless and hidden files.'
