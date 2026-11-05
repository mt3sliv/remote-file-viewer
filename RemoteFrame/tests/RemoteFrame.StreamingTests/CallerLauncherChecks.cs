using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RemoteFrame.AspNetCore;

internal static class CallerLauncherChecks
{
    internal static async Task ProbeAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var name = args[Array.IndexOf(args, "--pipe") + 1];
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(10000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var identity = WindowsIdentity.GetCurrent();
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            sid = identity.User!.Value, temporary = Path.GetTempPath(),
            session = Process.GetCurrentProcess().SessionId, arguments = args.Skip(1).Take(3).ToArray()
        }));
        var line = await reader.ReadLineAsync();
        if (line != "close") throw new Exception("Missing close message");
    }

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "RemoteFrame-launch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!);
            // The suite can be launched through dotnet or its apphost executable.
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(CallerLauncherChecks).Assembly.Location);
            start.ArgumentList.Add("--caller-launch-probe");
            string[] values = ["spaces and unicode é", "quote\"slash\\", "trailing\\"];
            foreach (var value in values) start.ArgumentList.Add(value);
            await CheckPipeTransportAsync(start, values);
            await CheckOfficeWorkerPipeAsync();
            using var current = WindowsIdentity.GetCurrent();
            // Use an impersonation token, matching the IIS request-token shape.
            using var caller = WindowsIdentity.RunImpersonated(current.AccessToken, () =>
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                return WindowsIdentity.GetCurrent();
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { using var unexpected = await WindowsCallerWorkerLauncher.LaunchAsync(start, caller, directory, false, cancelled.Token); throw new Exception("Cancelled launch succeeded."); }
            catch (OperationCanceledException) { Console.WriteLine("PASS: Cancelled token launch does not start a worker"); }
            var diagnostic = WindowsLaunchDiagnostics.Capture(current, caller, current.AccessToken, start.FileName, directory, false);
            Check(diagnostic.Contains("LogonId=0x") && diagnostic.Contains("TokenType=") && diagnostic.Contains("Host SeImpersonatePrivilege:") && diagnostic.Contains("WorkerReadableAsCaller=yes"),
                "Windows launch diagnostics inspect actual tokens, logon IDs, privilege state and caller file access");
            // Token process creation is a provisioned-host integration check. A generic
            // hosted runner can reject it with access denied as well as error 1314.
            // Keep transport/cancellation coverage unconditional and never suppress a
            // native launch error when this integration check is explicitly requested.
            if (Environment.GetEnvironmentVariable("REMOTEFRAME_REQUIRE_TOKEN_LAUNCH") != "1")
            {
                Console.WriteLine("SKIP: Native caller-token launch is an opt-in integration check. Set REMOTEFRAME_REQUIRE_TOKEN_LAUNCH=1 on a provisioned Windows host; all native launch errors then fail the suite.");
                return;
            }
            var connection = await WindowsCallerWorkerLauncher.LaunchAsync(start, caller, directory, false, deadline.Token);
            using (connection)
            {
                var line = await connection.Output.ReadLineAsync(deadline.Token) ?? throw new IOException("Caller probe exited without replying.");
                using var result = JsonDocument.Parse(line);
                var value = result.RootElement;
                Check(value.GetProperty("sid").GetString() == caller.User!.Value, "Native worker runs as token user");
                Check(value.GetProperty("session").GetInt32() == Process.GetCurrentProcess().SessionId, "Native worker stays in host session");
                Check(Path.TrimEndingDirectorySeparator(value.GetProperty("temporary").GetString()!) == Path.Combine(directory, "temp"), "Native worker gets session-local temporary storage");
                Check(value.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()).SequenceEqual(values), "Token-launch quoting preserves spaces, quotes and trailing backslashes");
                await connection.Input.WriteLineAsync("close");
                await connection.Input.FlushAsync();
                await connection.Process.WaitForExitAsync(deadline.Token);
                Check(connection.Process.ExitCode == 0, "Native worker pipe shuts down cleanly");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckPipeTransportAsync(ProcessStartInfo template, string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "RemoteFrame-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(template.FileName) { UseShellExecute = false, CreateNoWindow = true };
        start.Arguments = string.Join(" ", template.ArgumentList.Concat(["--pipe", name]).Select(WindowsCallerWorkerLauncher.Quote));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connected = pipe.WaitForConnectionAsync(deadline.Token);
        using var process = Process.Start(start) ?? throw new IOException("Cannot launch pipe transport probe");
        try
        {
            await connected;
            using var connection = new WorkerConnection(process, pipe, new CancellationTokenSource());
            using var result = JsonDocument.Parse(await connection.Output.ReadLineAsync(deadline.Token) ?? throw new IOException("Missing probe response"));
            Check(result.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()).SequenceEqual(arguments), "WorkerConnection exchanges JSON across a real named pipe");
            await connection.Input.WriteLineAsync("close"); await connection.Input.FlushAsync();
            await process.WaitForExitAsync(deadline.Token);
            Check(process.ExitCode == 0, "Named pipe close message allows orderly worker exit");
        }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }

    private static async Task CheckOfficeWorkerPipeAsync()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var worker = Path.Combine(root, "src/RemoteFrame.Office.Worker/bin", configuration, "net10.0/RemoteFrame.Office.Worker.exe");
        if (!File.Exists(worker)) throw new IOException("Build the Office worker in the same configuration before running the streaming suite.");
        var name = "RemoteFrame-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "artifacts/nonexistent-engine.exe"));
        start.ArgumentList.Add(Path.Combine(root, "artifacts/nonexistent-document.docx"));
        start.ArgumentList.Add("--bundled-only"); start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connected = pipe.WaitForConnectionAsync(deadline.Token);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start worker transport test");
        try
        {
            await connected;
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var result = JsonDocument.Parse(await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("Worker did not return its startup error over the pipe"));
            Check(result.RootElement.GetProperty("error").GetString()!.Contains("Office application"), "Office worker parses pipe options and returns startup errors over its pipe");
            Check(result.RootElement.GetProperty("stage").GetString() == "application-open" && result.RootElement.GetProperty("diagnostics").GetString()!.Contains("IOException"),
                "Office startup errors include stage and server diagnostics");
            await process.WaitForExitAsync(deadline.Token);
            Check(process.ExitCode == 1, "Office worker startup failure exits without opening an application");
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
