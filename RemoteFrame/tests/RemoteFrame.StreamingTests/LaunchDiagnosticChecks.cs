using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteFrame.AspNetCore;
using RemoteFrame.Core;

internal static class LaunchDiagnosticChecks
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemoteFrame-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var logs = new Logs();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); builder.Logging.AddProvider(logs);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IRenderProvider, FailedProvider>();
            builder.Services.AddRemoteFrame(options =>
            {
                options.RequireAuthorization = false;
                foreach (var id in new[] { "worker", "office" })
                {
                    var file = Path.Combine(directory, id + ".diagnostic");
                    File.WriteAllText(file, "test"); options.Files.Add(id, file);
                }
            });
            await using var app = builder.Build(); app.MapRemoteFrame(); await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            foreach (var id in new[] { "worker", "office" })
            {
                var response = await http.PostAsJsonAsync("/api/sessions", new { fileId = id });
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Launch failure returns HTTP 400: " + id);
                var content = await response.Content.ReadAsStringAsync();
                using var result = JsonDocument.Parse(content);
                var data = result.RootElement;
                Check(data.GetProperty("stage").GetString() == (id == "worker" ? "worker-process-create" : "application-open"), "Failure identifies worker launch versus Office initialization: " + id);
                var diagnosticId = data.GetProperty("diagnosticId").GetString();
                Check(!string.IsNullOrWhiteSpace(diagnosticId) && logs.Messages.Any(message => message.Contains(diagnosticId)), "Diagnostic ID correlates response with server log: " + id);
                Check(!content.Contains("PRIVATE-SERVER") && logs.Messages.Any(message => message.Contains("PRIVATE-SERVER")), "Detailed server diagnostics stay out of browser response: " + id);
                if (id == "worker") Check(data.GetProperty("nativeErrorCode").GetInt32() == 5 && data.GetProperty("error").GetString()!.Contains("have not started"), "Access-denied response preserves native code and explains Office was not started");
            }
            var denied = new CallerWorkerLaunchException(5, "diagnostics");
            Check(denied.NativeErrorCode == 5 && denied.Message.Contains("not proof of a missing SeImpersonatePrivilege"), "Error 5 does not assert a missing privilege");
            Check(new CallerWorkerLaunchException(1314, "diagnostics").PublicMessage.Contains("required host privilege"), "Missing-privilege error has distinct guidance");
            await app.StopAsync();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Check(bool result, string message)
    { if (!result) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private sealed class FailedProvider : IRenderProvider
    {
        public bool CanOpen(string path) => path.EndsWith(".diagnostic");
        public ValueTask<IRenderSession> OpenAsync(string path, CancellationToken ct) => Path.GetFileName(path) == "worker.diagnostic"
            ? ValueTask.FromException<IRenderSession>(new CallerWorkerLaunchException(5, "PRIVATE-SERVER token/path details"))
            : ValueTask.FromException<IRenderSession>(new OfficeApplicationOpenException("Office failed", "PRIVATE-SERVER application details"));
    }
    private sealed class Logs : ILoggerProvider
    {
        internal readonly System.Collections.Concurrent.ConcurrentBag<string> Messages = new();
        public ILogger CreateLogger(string name) => new Logger(this);
        public void Dispose() { }
        private sealed class Logger(Logs logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => logs.Messages.Add(formatter(state, exception) + exception);
        }
    }
}
