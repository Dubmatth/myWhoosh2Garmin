using Microsoft.Data.Sqlite;

namespace MyWhoosh2Garmin.Tracking;

public class SqliteActivityTracker : IActivityTracker, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteActivityTracker(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        using var createTable = _connection.CreateCommand();
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS processed_activities (
                file_hash TEXT PRIMARY KEY,
                file_name TEXT NOT NULL,
                garmin_activity_id TEXT,
                processed_at TEXT NOT NULL
            );
            """;
        createTable.ExecuteNonQuery();
    }

    public async Task<bool> IsProcessedAsync(string fileHash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM processed_activities WHERE file_hash = $hash";
        command.Parameters.AddWithValue("$hash", fileHash);
        object? result = await command.ExecuteScalarAsync();
        return result is not null;
    }

    public async Task MarkProcessedAsync(string fileHash, string fileName, string? garminActivityId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO processed_activities (file_hash, file_name, garmin_activity_id, processed_at)
            VALUES ($hash, $name, $activityId, $processedAt)
            """;
        command.Parameters.AddWithValue("$hash", fileHash);
        command.Parameters.AddWithValue("$name", fileName);
        command.Parameters.AddWithValue("$activityId", (object?)garminActivityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$processedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose() => _connection.Dispose();
}
