using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuickSwitch;

public record CacheAccount(string Pointer, string? BattleTag);

public sealed class ClientCache(string path)
{
    private const string PointerKey = "features_cached_data_points";
    public string FilePath => path;

    public CacheAccount? Match(string login, string region)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var db = Open(SqliteOpenMode.ReadOnly);
            var pointer = ReadPointer(db);
            if (pointer is null) return null;
            using var json = JsonDocument.Parse(pointer);
            if (!json.RootElement.TryGetProperty("account_id", out var account) ||
                !account.TryGetInt64(out var id) || id == 0) return null;
            using var command = db.CreateCommand();
            command.CommandText = "SELECT battle_tag, environment FROM login_cache WHERE account_id_lo = $id AND name = $name COLLATE NOCASE";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", AccountHash(login));
            using var rows = command.ExecuteReader();
            if (!rows.Read()) return null;
            var tag = rows.IsDBNull(0) ? null : rows.GetString(0);
            var environment = rows.IsDBNull(1) ? "" : rows.GetString(1);
            var knownRegion = LauncherConfig.Regions.FirstOrDefault(code =>
                environment.StartsWith(code + ".", StringComparison.OrdinalIgnoreCase));
            if (knownRegion is not null && knownRegion != region) return null;
            return new(pointer, tag);
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public string? ReadPointer()
    {
        if (!File.Exists(path)) return null;
        using var db = Open(SqliteOpenMode.ReadOnly);
        return ReadPointer(db);
    }

    public void WritePointer(string? value)
    {
        using var db = Open(SqliteOpenMode.ReadWrite);
        using var command = db.CreateCommand();
        using var transaction = db.BeginTransaction();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$key", PointerKey);
        if (value is null)
        {
            command.CommandText = "DELETE FROM key_value_store WHERE key = $key";
            command.ExecuteNonQuery();
        }
        else
        {
            command.Parameters.AddWithValue("$value", value);
            command.CommandText = "UPDATE key_value_store SET value = $value WHERE key = $key";
            if (command.ExecuteNonQuery() == 0)
            {
                command.CommandText = "INSERT INTO key_value_store(key, value) VALUES($key, $value)";
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private static string? ReadPointer(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT value FROM key_value_store WHERE key = $key";
        command.Parameters.AddWithValue("$key", PointerKey);
        return command.ExecuteScalar() as string;
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 2
        }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    public static string AccountHash(string text)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(text.ToUpperInvariant()))
            hash = unchecked((hash ^ b) * 0x100000001b3UL);
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
