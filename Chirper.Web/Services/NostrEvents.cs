using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NBitcoin.Secp256k1;

namespace Chirper.Web.Services;

/// <summary>
/// NIP-01 event verification for the Tellus Nostr logins (NIP-07 and NIP-46 produce the same signed
/// event): recomputes the canonical event id ([0, pubkey, created_at, kind, tags, content] with NIP-01
/// escaping), checks the BIP-340 schnorr signature, the auth kind (24242), and created_at freshness.
/// </summary>
internal static class NostrEvents
{
    public const int AuthKind = 24242;
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>Verify a signed auth event. Returns (ok, pubkeyHexLower, content, error).</summary>
    public static (bool Ok, string Pubkey, string Content, string Error) Verify(JsonElement evt)
    {
        if (evt.ValueKind != JsonValueKind.Object)
            return Fail("event object required");
        if (!TryGetString(evt, "id", out var id) ||
            !TryGetString(evt, "pubkey", out var pubkey) ||
            !TryGetString(evt, "sig", out var sig) ||
            !evt.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.Number || !kindEl.TryGetInt64(out var kind) ||
            !evt.TryGetProperty("created_at", out var atEl) || atEl.ValueKind != JsonValueKind.Number || !atEl.TryGetInt64(out var createdAt))
            return Fail("missing event fields");
        var content = evt.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? string.Empty : string.Empty;

        if (kind != AuthKind)
            return Fail($"wrong event kind (expected {AuthKind})");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (createdAt < now - (long)MaxClockSkew.TotalSeconds || createdAt > now + (long)MaxClockSkew.TotalSeconds)
            return Fail("event created_at too far from server time");
        if (pubkey.Length != 64 || !IsLowerHex(pubkey))
            return Fail("pubkey must be 64-char lower hex");

        // Canonical id: sha256 over the NIP-01 serialization array.
        if (!evt.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array ||
            tags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.Array || tag.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)))
            return Fail("tags must be arrays of strings");
        var canonical = Canonicalize(pubkey, createdAt, kind, tags, content);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        if (!Convert.ToHexString(hash).Equals(id, StringComparison.OrdinalIgnoreCase))
            return Fail("event id mismatch");

        byte[] pubkeyBytes, sigBytes;
        try
        {
            pubkeyBytes = Convert.FromHexString(pubkey);
            sigBytes = Convert.FromHexString(sig);
        }
        catch { return Fail("bad hex in pubkey/sig"); }
        if (sigBytes.Length != 64)
            return Fail("sig must be 64 bytes");
        if (!ECXOnlyPubKey.TryCreate(pubkeyBytes, out var xonly) || xonly is null)
            return Fail("invalid pubkey");
        if (!SecpSchnorrSignature.TryCreate(sigBytes, out var schnorr) || schnorr is null)
            return Fail("invalid signature encoding");
        if (!xonly.SigVerifyBIP340(schnorr, hash))
            return Fail("signature verification failed");

        return (true, pubkey.ToLowerInvariant(), content, string.Empty);

        static (bool, string, string, string) Fail(string error) => (false, string.Empty, string.Empty, error);
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsLowerHex(string s)
    {
        foreach (var ch in s)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }
        return true;
    }

    private static string Canonicalize(string pubkey, long createdAt, long kind, JsonElement tags, string content)
    {
        var sb = new StringBuilder(256);
        sb.Append("[0,\"").Append(pubkey.ToLowerInvariant()).Append("\",")
          .Append(createdAt).Append(',').Append(kind).Append(',');
        if (tags.ValueKind == JsonValueKind.Array)
        {
            sb.Append('[');
            var firstTag = true;
            foreach (var tag in tags.EnumerateArray())
            {
                if (!firstTag)
                    sb.Append(',');
                firstTag = false;
                sb.Append('[');
                var firstItem = true;
                foreach (var item in tag.EnumerateArray())
                {
                    if (!firstItem)
                        sb.Append(',');
                    firstItem = false;
                    AppendNip01String(sb, item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText());
                }
                sb.Append(']');
            }
            sb.Append(']');
        }
        else
        {
            sb.Append("[]");
        }
        sb.Append(',');
        AppendNip01String(sb, content);
        sb.Append(']');
        return sb.ToString();
    }

    // NIP-01: escape ONLY \n \" \\ \r \t \b \f; every other character verbatim UTF-8.
    private static void AppendNip01String(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\n':
                    sb.Append("\\n");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
