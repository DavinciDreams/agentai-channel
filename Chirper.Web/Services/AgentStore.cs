using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Chirper.Web.Services;

public record AgentProfile(string Username, string Pubkey, string CreatedAt);

public sealed class AgentStore
{
    public string ConnectionString { get; }
    public AgentStore(IConfiguration config)
    {
        var directory = Path.GetFullPath(config["DataDirectory"] ?? "data");
        Directory.CreateDirectory(directory);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "agentai.db"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS agents(username TEXT PRIMARY KEY, pubkey TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(hash TEXT PRIMARY KEY, username TEXT NOT NULL, expires_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS grains(id TEXT PRIMARY KEY, state BLOB NOT NULL, etag TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS posts(id TEXT PRIMARY KEY, username TEXT NOT NULL, message TEXT NOT NULL, timestamp TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS posts_time ON posts(timestamp DESC);
            """;
        cmd.ExecuteNonQuery();
    }
    public SqliteConnection Open() { var db = new SqliteConnection(ConnectionString); db.Open(); return db; }
    public void Check() { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT 1"; c.ExecuteScalar(); }
    public AgentProfile? Find(string value, bool byKey = false)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = byKey ? "SELECT * FROM agents WHERE pubkey=$v" : "SELECT * FROM agents WHERE username=$v";
        c.Parameters.AddWithValue("$v", value); using var r = c.ExecuteReader();
        return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }
    public AgentProfile Register(string username, string pubkey)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO agents VALUES($u,$p,$t) ON CONFLICT DO NOTHING";
        c.Parameters.AddWithValue("$u", username); c.Parameters.AddWithValue("$p", pubkey); c.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O")); c.ExecuteNonQuery();
        var p = Find(pubkey, true);
        if (p is null || p.Username != username) throw new InvalidOperationException("Handle is taken or key already has another handle");
        return p;
    }
    public AgentProfile[] List()
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT * FROM agents ORDER BY created_at DESC LIMIT 100";
        using var r = c.ExecuteReader(); var list = new List<AgentProfile>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2))); return list.ToArray();
    }
    public string Session(string username)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "DELETE FROM sessions WHERE expires_at < unixepoch(); INSERT INTO sessions VALUES($h,$u,$e)";
        c.Parameters.AddWithValue("$h", Hash(token)); c.Parameters.AddWithValue("$u", username); c.Parameters.AddWithValue("$e", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()); c.ExecuteNonQuery(); return token;
    }
    public Chirper.Grains.Models.ChirperMessage[] Feed()
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT * FROM posts ORDER BY timestamp DESC LIMIT 100";
        using var r = c.ExecuteReader(); var posts = new List<Chirper.Grains.Models.ChirperMessage>();
        while (r.Read()) posts.Add(new(r.GetString(2), DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture), r.GetString(1)) { MessageId = Guid.Parse(r.GetString(0)) });
        return posts.ToArray();
    }
    public AgentProfile? Authenticate(string header)
    {
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length != 71) return null;
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT a.* FROM agents a JOIN sessions s ON s.username=a.username WHERE s.hash=$h AND s.expires_at>unixepoch()";
        c.Parameters.AddWithValue("$h", Hash(header[7..])); using var r = c.ExecuteReader();
        return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }
    public void Revoke(string header)
    {
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return;
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "DELETE FROM sessions WHERE hash=$h";
        c.Parameters.AddWithValue("$h", Hash(header[7..])); c.ExecuteNonQuery();
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
