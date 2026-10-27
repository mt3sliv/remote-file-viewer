param(
    [Parameter(Mandatory=$true)][string]$Engine,
    [Parameter(Mandatory=$true)][string]$Worker,
    [switch]$EditAndSave,
    [string]$Sample = 'TestLibreOfficeWriterDocument.odt'
)
$ErrorActionPreference = 'Stop'
$remoteFrameRoot = Split-Path $PSScriptRoot -Parent
$probeDir = Join-Path $remoteFrameRoot 'artifacts/office-probe'
New-Item -ItemType Directory -Force $probeDir | Out-Null
$extension = [IO.Path]::GetExtension($Sample)
$probeFile = Join-Path $probeDir "probe$extension"
Copy-Item -LiteralPath (Join-Path $remoteFrameRoot "samples/$Sample") -Destination $probeFile -Force
$info = [System.Diagnostics.ProcessStartInfo]::new($Worker)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.ArgumentList.Add($Engine)
$info.ArgumentList.Add($probeFile)
$probe = [System.Diagnostics.Process]::Start($info)
function Send-Command($command) {
    $probe.StandardInput.WriteLine(($command | ConvertTo-Json -Compress))
    $reply = $probe.StandardOutput.ReadLine() | ConvertFrom-Json
    if ($null -eq $reply) { throw 'Worker exited without a reply' }
    if ($reply.error) { throw $reply.error }
    return $reply
}
try {
    $reply = Send-Command @{kind='capture'}
    [System.IO.File]::WriteAllBytes("$probeDir/initial.png", [Convert]::FromBase64String($reply.png))
    Write-Output "Captured $($reply.width)x$($reply.height) private desktop frame"
    if ($EditAndSave) {
        Send-Command @{kind='pointerdown';x=500;y=300;button=0} | Out-Null
        Send-Command @{kind='pointerup';x=500;y=300;button=0} | Out-Null
        Start-Sleep -Milliseconds 200
        Send-Command @{kind='key';key='End';ctrl=$true} | Out-Null
        Send-Command @{kind='key';key='Enter'} | Out-Null
        foreach ($character in 'RemoteFrame native save probe'.ToCharArray()) {
            Send-Command @{kind='key';key=[string]$character} | Out-Null
        }
        if ($extension -eq '.ods') { Send-Command @{kind='key';key='Enter'} | Out-Null }
        $reply = Send-Command @{kind='capture'}
        [System.IO.File]::WriteAllBytes("$probeDir/typed.png", [Convert]::FromBase64String($reply.png))
        Send-Command @{kind='save'} | Out-Null
        Start-Sleep -Milliseconds 1500
        $reply = Send-Command @{kind='capture'}
        [System.IO.File]::WriteAllBytes("$probeDir/edited.png", [Convert]::FromBase64String($reply.png))
        $zip = [System.IO.Compression.ZipFile]::OpenRead($probeFile)
        try {
            $reader = [System.IO.StreamReader]::new($zip.GetEntry('content.xml').Open())
            try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
        if (-not $content.Contains('RemoteFrame native save probe')) { throw "Edited text was not persisted to the $extension file" }
        Write-Output 'PASS: Native text input and Save persisted the edit to the copied document.'
    }
} finally {
    $probe.StandardInput.Close()
    if (-not $probe.WaitForExit(5000)) { $probe.Kill($true) }
    $probe.Dispose()
}
