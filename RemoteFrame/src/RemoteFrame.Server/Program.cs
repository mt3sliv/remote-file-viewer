using RemoteFrame.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
// This anonymous example deliberately listens only on loopback.
var demoPort = builder.Configuration.GetValue<int?>("RemoteFrame:DemoPort") ?? 5088;
if (demoPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(demoPort));
builder.WebHost.UseUrls($"http://127.0.0.1:{demoPort}");
builder.Services.AddRemoteFrame(options =>
{
    options.RequireAuthorization = false;
    options.EnableOffice = true;
    var samples = Path.Combine(AppContext.BaseDirectory, "samples");
    foreach (var file in Directory.EnumerateFiles(samples).OrderBy(Path.GetFileName))
        options.Files.Add(Path.GetFileName(file), file);
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))
    { context.Response.StatusCode = 403; return; }
    await next(context);
});
app.MapGet("/", (Microsoft.Extensions.Options.IOptions<RemoteFrameOptions> options) =>
{
    static string Encode(string value) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(value);
    var links = string.Join("", options.Value.Files.Keys.OrderBy(id => id).Select(id =>
        $"<li><a href=\"/viewer?fileId={Encode(Uri.EscapeDataString(id))}\" target=\"_blank\" rel=\"noopener\">Open {Encode(id)}</a></li>"));
    return Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>RemoteFrame demo files</title></head><body><h1>Demo files</h1><p>Choose a file to open in a new viewer tab. Saved Office edits update the demo output copy.</p><ul>" + links + "</ul></body></html>", "text/html; charset=utf-8");
});
app.UseWebSockets();
app.MapRemoteFrame();
app.Run();
