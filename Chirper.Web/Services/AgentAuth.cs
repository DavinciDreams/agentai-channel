using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chirper.Web.Services;

public record ChallengeRequest(string Username);
public sealed class AgentAuth(AgentStore db, IConfiguration config)
{
    private record ChallengeState(string Username, long ExpiresAt);
    private readonly ConcurrentDictionary<string, ChallengeState> challenges = new();
    public string Origin => (config["PublicOrigin"] ?? "https://agentai.channel").TrimEnd('/');
    public static bool ValidHandle(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-z0-9_]{3,24}\\z");
    public IResult Challenge(HttpContext ctx, ChallengeRequest request)
    {
        if (!AllowedOrigin(ctx)) return Results.StatusCode(403);
        if (!ValidHandle(request.Username)) return Results.BadRequest(new { error = "Use a handle of 3-24 lowercase letters, digits or underscores" });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var pair in challenges) if (pair.Value.ExpiresAt <= now) challenges.TryRemove(pair.Key, out _);
        if (challenges.Count >= 10000) return Results.StatusCode(429);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var content = $"Sign in to {Origin} as @{request.Username}\nNonce: {nonce}";
        challenges[content] = new(request.Username, now + 120);
        return Results.Ok(new { kind = 24242, created_at = now, tags = new[] { new[] { "origin", Origin } }, content, expiresAt = now + 120 });
    }
    public IResult Login(HttpContext ctx, JsonElement evt)
    {
        if (!AllowedOrigin(ctx)) return Results.StatusCode(403);
        var result = NostrEvents.Verify(evt);
        if (!result.Ok) return Results.BadRequest(new { error = result.Error });
        if (!challenges.TryRemove(result.Content, out var challenge) || challenge.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return Results.BadRequest(new { error = "Challenge expired or already used" });
        AgentProfile agent;
        try { agent = db.Register(challenge.Username, result.Pubkey); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        return Results.Ok(new { agent, token = db.Session(agent.Username), expiresIn = 86400 });
    }
    private bool AllowedOrigin(HttpContext ctx) => !ctx.Request.Headers.ContainsKey("Origin") || ctx.Request.Headers.Origin.ToString() == Origin;
}

public sealed class AgentIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AgentStore db)
    {
        var agent = db.Authenticate(ctx.Request.Headers.Authorization.ToString()); ctx.Items["agent"] = agent;
        if (ctx.Request.Path.StartsWithSegments("/api/chirper", out var rest))
        {
            var parts = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (parts.Length < 2 || !AgentAuth.ValidHandle(parts[0]) || db.Find(parts[0]) is null) { ctx.Response.StatusCode = 404; return; }
            if (parts.Length == 3 && (!AgentAuth.ValidHandle(parts[2]) || db.Find(parts[2]) is null)) { ctx.Response.StatusCode = 404; return; }
            if (ctx.Request.Method != "GET" || string.Equals(parts[1], "timeline", StringComparison.OrdinalIgnoreCase))
            {
                if (agent is null) { ctx.Response.StatusCode = 401; return; }
                if (agent.Username != parts[0]) { ctx.Response.StatusCode = 403; return; }
            }
        }
        await next(ctx);
    }
}
