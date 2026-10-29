param([string]$Worker = "$PSScriptRoot/../artifacts/installed-office-worker/RemoteFrame.Office.Worker.exe")
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/installed-excel-probe'))
New-Item -ItemType Directory -Force $root | Out-Null
$document = Join-Path $root 'isolated workbook.xlsx'
# A disposable workbook: never open a user's original document or existing Excel process.
$entries = @{
    '[Content_Types].xml' = '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>'
    '_rels/.rels' = '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
    'xl/workbook.xml' = '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>'
    'xl/_rels/workbook.xml.rels' = '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>'
    'xl/worksheets/sheet1.xml' = '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Initial</t></is></c></row></sheetData></worksheet>'
}
if (Test-Path -LiteralPath $document) { Remove-Item -LiteralPath $document }
$zip = [IO.Compression.ZipFile]::Open($document, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $entries.GetEnumerator()) {
        $writer = [IO.StreamWriter]::new($zip.CreateEntry($entry.Key).Open())
        try { $writer.Write($entry.Value) } finally { $writer.Dispose() }
    }
} finally { $zip.Dispose() }
$before = (Get-FileHash -LiteralPath $document).Hash
$info = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Worker))
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.ArgumentList.Add((Join-Path $root 'absent-bundled-engine.exe'))
$info.ArgumentList.Add($document)
$process = [Diagnostics.Process]::Start($info)
function Send-Probe($value) {
    $process.StandardInput.WriteLine(($value | ConvertTo-Json -Compress))
    $read = $process.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(90000)) { throw 'Installed Excel worker timed out.' }
    $reply = $read.Result | ConvertFrom-Json
    if (-not $reply -or $reply.error) { throw "Excel probe failed: $($reply.error)" }
    return $reply
}
try {
    $frame = Send-Probe @{kind='capture'}
    if ($frame.application -ne 'Excel') { throw "Expected installed Excel, received $($frame.application)" }
    [IO.File]::WriteAllBytes((Join-Path $root 'initial.png'), [Convert]::FromBase64String($frame.png))
    if ((Get-FileHash -LiteralPath $document).Hash -ne $before) { throw 'Opening modified the original workbook.' }
    Send-Probe @{kind='configure';configuration=@{framesPerSecond=10;captureWidth=960;captureHeight=600;streamWidth=480;streamHeight=300}} | Out-Null
    $frame = Send-Probe @{kind='capture'}
    if ($frame.width -ne 480 -or $frame.height -ne 300) { throw 'Installed Excel resize failed.' }
    Send-Probe @{kind='key';key='Home';ctrl=$true} | Out-Null
    foreach ($character in 'RemoteFrameExcelProbe'.ToCharArray()) { Send-Probe @{kind='key';key=[string]$character} | Out-Null }
    Send-Probe @{kind='key';key='Enter'} | Out-Null
    Send-Probe @{kind='save'} | Out-Null
    $saved = $false
    for ($attempt=0; $attempt -lt 15; $attempt++) {
        Start-Sleep -Milliseconds 400
        Send-Probe @{kind='capture'} | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($document)
        try {
            foreach ($entry in $zip.Entries | Where-Object FullName -Like '*.xml') {
                $reader = [IO.StreamReader]::new($entry.Open())
                try { if ($reader.ReadToEnd().Contains('RemoteFrameExcelProbe')) { $saved=$true } } finally { $reader.Dispose() }
            }
        } finally { $zip.Dispose() }
        if ($saved) { break }
    }
    if (-not $saved) { throw 'Installed Excel input/save did not persist the edit.' }
    Write-Output 'PASS: Installed Excel selected without a bundled engine; isolated capture, resize, keyboard input and save succeeded.'
} finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
