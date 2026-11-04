using Microsoft.AspNetCore.Http;
using RemoteFrame.AspNetCore;
using RemoteFrame.Core;
using System.Security.Principal;

internal static class IdentityChecks
{
    public static async Task RunAsync()
    {
        var source = Path.GetTempFileName();
        try
        {
            File.WriteAllText(source, "original");
            var provider = new Provider();
            var context = new DefaultHttpContext();
            var options = new RemoteFrameOptions();
            var session = await IdentityRenderSession.OpenAsync(provider, source, context, options, default);
            var staged = provider.Path!;
            Assert(staged != source && File.ReadAllText(staged) == "original", "Source is staged separately");
            File.WriteAllText(staged, "saved");
            await session.CaptureAsync(default);
            Assert(File.ReadAllText(source) == "saved", "Saved local changes reach source");
            File.WriteAllText(source, "external");
            File.WriteAllText(staged, "conflicting");
            await RejectAsync(async () => { await session.CaptureAsync(default); }, typeof(IOException));
            Assert(File.ReadAllText(source) == "external", "Conflicting source is preserved");
            await RejectAsync(async () => { await session.DisposeAsync(); }, typeof(IOException));
            await session.DisposeAsync();
            Assert(!Directory.Exists(System.IO.Path.GetDirectoryName(staged)), "Staging cleaned after conflict");
            options.FileAccess.StageLocally = false;
            await using (await IdentityRenderSession.OpenAsync(provider, source, context, options, default))
                Assert(provider.Path == source, "Service direct mode preserves original path");
            options.FileAccess.StageLocally = true;
            foreach (var selection in new[] { "download", "save", "process" })
            {
                options.FileAccess.DownloadIdentity = selection == "download" ? FileAccessIdentity.Caller : FileAccessIdentity.Service;
                options.FileAccess.SaveBackIdentity = selection == "save" ? FileAccessIdentity.Caller : FileAccessIdentity.Service;
                options.ApplicationSession.ProcessIdentity = selection == "process" ? FileAccessIdentity.Caller : FileAccessIdentity.Service;
                var count = provider.Opens;
                await RejectAsync(async () => { await IdentityRenderSession.OpenAsync(provider, source, context, options, default); }, typeof(InvalidOperationException));
                Assert(provider.Opens == count, "Missing caller fails before provider opens: " + selection);
            }
            if (OperatingSystem.IsWindows())
            {
                var identity = WindowsIdentity.GetCurrent();
                context.User = new WindowsPrincipal(identity);
                options.ApplicationSession.ProcessIdentity = FileAccessIdentity.Service;
                options.FileAccess.DownloadIdentity = FileAccessIdentity.Caller;
                options.FileAccess.SaveBackIdentity = FileAccessIdentity.Caller;
                var callerSession = await IdentityRenderSession.OpenAsync(provider, source, context, options, default);
                identity.Dispose(); // Opening request can end before save-back.
                File.WriteAllText(provider.Path!, "caller save");
                await callerSession.CaptureAsync(default);
                Assert(File.ReadAllText(source) == "caller save", "Duplicated Windows caller token survives request identity disposal");
                await callerSession.DisposeAsync();
                using var nextIdentity = WindowsIdentity.GetCurrent();
                context.User = new WindowsPrincipal(nextIdentity);
                options.ApplicationSession.ProcessIdentity = FileAccessIdentity.Caller;
                var count = provider.Opens;
                await RejectAsync(async () => { await IdentityRenderSession.OpenAsync(provider, source, context, options, default); }, typeof(InvalidOperationException));
                Assert(provider.Opens == count, "Caller process cannot silently use service launcher");
            }
            Console.WriteLine("All identity checks passed.");
        }
        finally { File.Delete(source); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
    private static async Task RejectAsync(Func<Task> action, Type exception)
    {
        try { await action(); }
        catch (Exception ex) when (ex.GetType() == exception) { return; }
        throw new Exception("Expected " + exception.Name);
    }
    private sealed class Provider : IRenderProvider
    {
        public string? Path;
        public int Opens;
        public bool CanOpen(string path) => true;
        public ValueTask<IRenderSession> OpenAsync(string path, CancellationToken ct)
        { Path = path; Opens++; return ValueTask.FromResult<IRenderSession>(new Session()); }
    }
    private sealed class Session : IRenderSession
    {
        public bool CanSave => true;
        public ValueTask<Frame> CaptureAsync(CancellationToken ct) => ValueTask.FromResult(new Frame([], 1, 1));
        public ValueTask ApplyAsync(InputEvent input, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
