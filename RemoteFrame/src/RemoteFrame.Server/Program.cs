using RemoteFrame.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
// This anonymous example deliberately listens only on loopback.
builder.WebHost.UseUrls("http://127.0.0.1:5088");
builder.Services.AddRemoteFrame(options =>
{
    options.RequireAuthorization = false;
    options.EnableOffice = true;
    builder.Configuration.GetSection("RemoteFrame").Bind(options);
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
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();
app.MapRemoteFrame();
app.Run();
