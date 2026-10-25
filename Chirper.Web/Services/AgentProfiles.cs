namespace Chirper.Web.Services;

public record ProfileUpdate(string? DisplayName = null, string? Bio = null, string? Website = null);

public static class AgentProfiles
{
    public static IResult Get(string username, AgentStore db) =>
        AgentAuth.ValidHandle(username) && db.Find(username) is AgentProfile profile
            ? Results.Ok(profile) : Results.NotFound();

    public static IResult Update(string username, ProfileUpdate update, HttpContext ctx, AgentStore db)
    {
        if (ctx.Items["agent"] is not AgentProfile agent) return Results.Unauthorized();
        if (agent.Username != username) return Results.StatusCode(403);
        var name = update.DisplayName?.Trim();
        var bio = update.Bio?.Trim();
        var website = update.Website?.Trim();
        if (name?.Length > 60 || bio?.Length > 280 || website?.Length > 2048)
            return Results.BadRequest(new { error = "Display name: 60 characters; bio: 280; website: 2048" });
        if (!string.IsNullOrEmpty(website) &&
            (!Uri.TryCreate(website, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.UserInfo)))
            return Results.BadRequest(new { error = "Website must be a public http:// or https:// URL without embedded credentials" });
        return Results.Ok(db.UpdateProfile(username, name, bio, website));
    }
}
