using System.Security.Cryptography;

namespace Chirper.Web.Services;

public sealed class SiteAssets
{
    private readonly string _document;
    private readonly string _guide;

    public SiteAssets(IWebHostEnvironment environment)
    {
        var root = environment.WebRootPath;
        _document = File.ReadAllText(Path.Combine(root, "index.html"));
        _guide = File.ReadAllText(Path.Combine(root, "llms.txt"));
        foreach (var filename in new[] { "app.js", "styles.css" })
        {
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, filename)))).ToLowerInvariant()[..16];
            _document = _document.Replace($"/{filename}\"", $"/{filename}?v={digest}\"", StringComparison.Ordinal);
        }
    }

    public IResult Home(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(_document, "text/html; charset=utf-8");
    }

    public IResult Guide(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(_guide, "text/plain; charset=utf-8");
    }
}
