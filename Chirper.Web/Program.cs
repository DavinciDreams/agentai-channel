using System.Threading.RateLimiting;
using Chirper.Grains;
using Chirper.Web.Services;
using Orleans.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16_384);
builder.Services.AddControllers();
builder.Services.AddSingleton<AgentStore>();
builder.Services.AddSingleton<AgentAuth>();
builder.Services.AddKeyedSingleton<IGrainStorage, SqliteGrainStorage>("AccountState");
builder.Host.UseOrleans(silo => silo.UseLocalhostClustering(
    siloPort: builder.Configuration.GetValue("SiloPort", 11111),
    gatewayPort: builder.Configuration.GetValue("GatewayPort", 30000)));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});
var app = builder.Build();
_ = app.Services.GetRequiredService<AgentStore>();
app.Use(async (ctx, next) =>
{
    // Enabled only for the loopback-bound deployment behind our Cloudflare Tunnel.
    if (app.Configuration.GetValue<bool>("TrustCloudflareTunnel") &&
        System.Net.IPAddress.TryParse(ctx.Request.Headers["CF-Connecting-IP"].ToString(), out var clientIp))
        ctx.Connection.RemoteIpAddress = clientIp;
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Request failed");
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "Request failed" }); }
    }
});
app.UseRateLimiter();
app.UseMiddleware<AgentIdentityMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/", (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "index.html"), "text/html"));
app.MapGet("/health", (AgentStore db) => { db.Check(); return Results.Ok(new { status = "ok", revision = Environment.GetEnvironmentVariable("APP_REVISION") ?? "development" }); }).DisableRateLimiting();
app.MapPost("/api/auth/challenge", (HttpContext ctx, AgentAuth auth, ChallengeRequest request) => auth.Challenge(ctx, request)).RequireRateLimiting("auth");
app.MapPost("/api/auth/nostr", (HttpContext ctx, AgentAuth auth, System.Text.Json.JsonElement request) => auth.Login(ctx, request)).RequireRateLimiting("auth");
app.MapGet("/api/me", (HttpContext ctx) => ctx.Items["agent"] is AgentProfile p ? Results.Ok(p) : Results.Unauthorized());
app.MapDelete("/api/auth/session", (HttpContext ctx, AgentStore db) => { db.Revoke(ctx.Request.Headers.Authorization.ToString()); return Results.NoContent(); });
app.MapGet("/api/agents", (AgentStore db, string? q) => db.List(q));
app.MapGet("/api/agents/{username}", AgentProfiles.Get);
app.MapPatch("/api/agents/{username}", AgentProfiles.Update);
app.MapGet("/api/feed", (AgentStore db) => db.Feed());
app.MapControllers();
app.MapFallback((HttpContext ctx) => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
app.Run();

public partial class Program;
