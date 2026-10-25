using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace Chirper.Web.Services;

// One Orleans silo, backed by a persistent SQLite volume. ETags reject stale writes.
public sealed class SqliteGrainStorage(AgentStore db, Serializer serializer) : IGrainStorage
{
    private static string Key(string type, GrainId id) => type + ":" + id;
    public Task ReadStateAsync<T>(string type, GrainId id, IGrainState<T> state)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT state,etag FROM grains WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", Key(type, id)); using var r = cmd.ExecuteReader();
        if (r.Read()) { state.State = serializer.Deserialize<T>((byte[])r[0]); state.ETag = r.GetString(1); state.RecordExists = true; }
        return Task.CompletedTask;
    }
    public Task WriteStateAsync<T>(string type, GrainId id, IGrainState<T> state)
    {
        var bytes = serializer.SerializeToArray(state.State); var etag = Guid.NewGuid().ToString("N");
        using var c = db.Open(); using var transaction = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO grains VALUES($i,$s,$e) ON CONFLICT(id) DO UPDATE SET state=$s,etag=$e WHERE grains.etag=$old";
        cmd.Parameters.AddWithValue("$i", Key(type, id)); cmd.Parameters.AddWithValue("$s", bytes); cmd.Parameters.AddWithValue("$e", etag); cmd.Parameters.AddWithValue("$old", (object?)state.ETag ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() != 1) throw new InconsistentStateException("Stale grain ETag");
        if (state.State is Chirper.Grains.ChirperAccountState account)
        {
            foreach (var post in account.MyPublishedMessages)
            {
                using var insert = c.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO posts VALUES($i,$u,$m,$t)";
                insert.Parameters.AddWithValue("$i", post.MessageId.ToString()); insert.Parameters.AddWithValue("$u", post.PublisherUserName);
                insert.Parameters.AddWithValue("$m", post.Message); insert.Parameters.AddWithValue("$t", post.Timestamp.ToUniversalTime().ToString("O")); insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        state.ETag = etag; state.RecordExists = true; return Task.CompletedTask;
    }
    public Task ClearStateAsync<T>(string type, GrainId id, IGrainState<T> state)
    {
        using var c = db.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM grains WHERE id=$i AND etag=$e";
        cmd.Parameters.AddWithValue("$i", Key(type, id)); cmd.Parameters.AddWithValue("$e", (object?)state.ETag ?? DBNull.Value); cmd.ExecuteNonQuery(); state.ETag = null; state.RecordExists = false; return Task.CompletedTask;
    }
}
