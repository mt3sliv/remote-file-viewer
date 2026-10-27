$ErrorActionPreference = 'Stop'
$remoteFrameRoot = Split-Path $PSScriptRoot -Parent
$server = Join-Path $remoteFrameRoot 'src/RemoteFrame.Server'
$output = Join-Path $remoteFrameRoot 'artifacts/demo-probe'
New-Item -ItemType Directory -Force $output | Out-Null
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(120)
$client.BaseAddress = [Uri]'http://127.0.0.1:5088'
$listener = [Net.Sockets.TcpClient]::new()
try { $listener.Connect('127.0.0.1',5088); throw 'Port 5088 is already in use; stop the existing demo before running this test.' }
catch [Net.Sockets.SocketException] { }
finally { $listener.Dispose() }
$process = Start-Process -FilePath "$server/bin/Debug/net10.0/RemoteFrame.Server.exe" -WorkingDirectory $server -WindowStyle Hidden -RedirectStandardOutput "$output/server.log" -RedirectStandardError "$output/server-error.log" -PassThru
try {
    $files = $null
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        if ($process.HasExited) { throw "Demo exited: $(Get-Content "$output/server-error.log" -Raw)" }
        try {
            $response = $client.GetAsync('/api/files').GetAwaiter().GetResult()
            $response.EnsureSuccessStatusCode() | Out-Null
            $files = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            break
        } catch { Start-Sleep -Milliseconds 200 }
    }
    if ($null -eq $files) { throw 'Demo did not become ready' }
    $expected = Get-ChildItem -LiteralPath "$remoteFrameRoot/samples" -File | Select-Object -ExpandProperty Name
    if (Compare-Object ($files | Sort-Object) ($expected | Sort-Object)) { throw 'Sample allowlist does not match the samples directory' }
    $html = $client.GetStringAsync('/').GetAwaiter().GetResult()
    if ($html.Contains('id="login"') -or $html.Contains('id="key"')) { throw 'Demo still has login controls' }
    Write-Output 'PASS: All samples are listed without login; API-key UI is absent.'
    foreach ($file in $files) {
        $json = @{fileId=$file} | ConvertTo-Json -Compress
        $request = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
        $response = $client.PostAsync('/api/sessions', $request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "Cannot open ${file}: $body" }
        $id = ($body | ConvertFrom-Json).id
        $socket = [Net.WebSockets.ClientWebSocket]::new()
        $socket.Options.SetRequestHeader('Origin','http://127.0.0.1:5088')
        $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(45))
        try {
            $socket.ConnectAsync([Uri]"ws://127.0.0.1:5088/ws/$id", $timeout.Token).GetAwaiter().GetResult() | Out-Null
            $bytes = [byte[]]::new(65536)
            $frame = [IO.MemoryStream]::new()
            try {
                do {
                    $message = $socket.ReceiveAsync([ArraySegment[byte]]::new($bytes), $timeout.Token).GetAwaiter().GetResult()
                    if ($message.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Binary) { throw 'Expected a PNG frame' }
                    $frame.Write($bytes, 0, $message.Count)
                } while (-not $message.EndOfMessage)
                $png = $frame.ToArray()
                if ([BitConverter]::ToString($png,0,8) -ne '89-50-4E-47-0D-0A-1A-0A') { throw 'Invalid PNG frame' }
                [IO.File]::WriteAllBytes((Join-Path $output "$file.png"),$png)
            } finally { $frame.Dispose() }
            Write-Output "PASS: $file opens and streams PNG frames through the packaged middleware."
        } finally {
            $socket.Abort(); $socket.Dispose(); $timeout.Dispose()
            $client.DeleteAsync("/api/sessions/$id").GetAwaiter().GetResult().EnsureSuccessStatusCode() | Out-Null
        }
    }
} finally {
    $client.Dispose()
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
