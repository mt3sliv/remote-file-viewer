using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace RemoteFrame.AspNetCore;

internal static class RemoteFrameViewer
{
    private static readonly string Html = Read("index.html");
    private static readonly string Script = Read("viewer.js");
    private static readonly string Styles = Read("viewer.css");

    public static void Map(RouteGroupBuilder routes)
    {
        // Same route group, authorization policies and PathBase as the API and socket.
        // GET renders a shell only; the client creates the session via the existing POST.
        routes.MapGet("/viewer", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' blob:; base-uri 'none'; form-action 'self'; frame-ancestors 'self'";
            var path = context.Request.Path.Value!;
            var prefix = context.Request.PathBase + path[..^"/viewer".Length];
            var tokens = context.RequestServices.GetService<IAntiforgery>()?.GetAndStoreTokens(context);
            string Encode(string? value) => HtmlEncoder.Default.Encode(value ?? "");
            return Results.Content(Html.Replace("{{BASE}}", Encode(prefix))
                .Replace("{{CSRF_HEADER}}", Encode(tokens?.HeaderName))
                .Replace("{{CSRF_TOKEN}}", Encode(tokens?.RequestToken)), "text/html; charset=utf-8");
        });
        routes.MapGet("/viewer.js", (HttpContext context) => Asset(context, Script, "text/javascript; charset=utf-8"));
        routes.MapGet("/viewer.css", (HttpContext context) => Asset(context, Styles, "text/css; charset=utf-8"));
    }

    private static IResult Asset(HttpContext context, string content, string type)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Content(content, type);
    }

    private static string Read(string name)
    {
        using var stream = typeof(RemoteFrameViewer).Assembly.GetManifestResourceStream("RemoteFrame.Viewer." + name)
            ?? throw new InvalidOperationException("Missing embedded RemoteFrame viewer asset: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
