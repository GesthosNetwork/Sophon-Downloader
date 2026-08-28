using Microsoft.Data.Sqlite;

namespace SophonDownloader.Services;

internal sealed class DownloadHistoryStore
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly string _connectionString;

    public DownloadHistoryStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));

        _connectionString = $"Data Source={Path.GetFullPath(databasePath)}";
    }

    public void Initialize()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = """
                CREATE TABLE IF NOT EXISTS download_history
                (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    type INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    destination_directory TEXT NOT NULL,
                    legacy_urls TEXT NOT NULL,
                    game_id TEXT NULL,
                    game_display_name TEXT NULL,
                    game_region TEXT NULL,
                    version TEXT NULL,
                    channel TEXT NULL,
                    delete_chunks_after_extraction INTEGER NOT NULL,
                    selected_category_ids TEXT NOT NULL,
                    selected_content_names TEXT NOT NULL DEFAULT '',
                    state INTEGER NOT NULL,
                    status_message TEXT NULL
                );
                """;

            command.ExecuteNonQuery();
            EnsureColumn(connection, "selected_content_names", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "patch_from_version", "TEXT NULL");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize download history database.");
        }
    }

    public List<DownloadHistoryEntry> LoadEntries()
    {
        var entries = new List<DownloadHistoryEntry>();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT type, title, destination_directory, legacy_urls,
                   game_id, game_display_name, game_region, version, channel,
                   delete_chunks_after_extraction, selected_category_ids,
                   selected_content_names, patch_from_version, state, status_message
            FROM download_history
            ORDER BY id ASC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            entries.Add(new DownloadHistoryEntry
            {
                Type = reader.GetInt32(0),
                Title = reader.GetString(1),
                DestinationDirectory = reader.GetString(2),
                LegacyUrls = ReadStringList(reader.GetString(3)),
                GameId = ReadNullableString(reader, 4),
                GameDisplayName = ReadNullableString(reader, 5),
                GameRegion = ReadNullableString(reader, 6),
                Version = ReadNullableString(reader, 7),
                Channel = ReadNullableString(reader, 8),
                DeleteChunksAfterExtraction = reader.GetInt32(9) != 0,
                SelectedCategoryIds = ReadStringList(reader.GetString(10)),
                SelectedContentNames = ReadStringList(reader.GetString(11)),
                PatchFromVersion = ReadNullableString(reader, 12),
                State = reader.GetInt32(13),
                StatusMessage = ReadNullableString(reader, 14)
            });
        }

        return entries;
    }

    public void Save(IEnumerable<DownloadHistoryEntry> entries)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "DELETE FROM download_history;";
        command.ExecuteNonQuery();

        foreach (DownloadHistoryEntry entry in entries)
            InsertEntry(connection, transaction, entry);

        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void InsertEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadHistoryEntry entry)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO download_history
            (
                type, title, destination_directory, legacy_urls,
                game_id, game_display_name, game_region, version, channel,
                delete_chunks_after_extraction, selected_category_ids,
                selected_content_names, patch_from_version, state, status_message
            )
            VALUES
            (
                $type, $title, $destination_directory, $legacy_urls,
                $game_id, $game_display_name, $game_region, $version, $channel,
                $delete_chunks_after_extraction, $selected_category_ids,
                $selected_content_names, $patch_from_version, $state, $status_message
            );
            """;

        AddParameter(command, "$type", entry.Type);
        AddParameter(command, "$title", entry.Title);
        AddParameter(command, "$destination_directory", entry.DestinationDirectory);
        AddParameter(command, "$legacy_urls", WriteStringList(entry.LegacyUrls));
        AddParameter(command, "$game_id", entry.GameId);
        AddParameter(command, "$game_display_name", entry.GameDisplayName);
        AddParameter(command, "$game_region", entry.GameRegion);
        AddParameter(command, "$version", entry.Version);
        AddParameter(command, "$channel", entry.Channel);
        AddParameter(command, "$delete_chunks_after_extraction", entry.DeleteChunksAfterExtraction ? 1 : 0);
        AddParameter(command, "$selected_category_ids", WriteStringList(entry.SelectedCategoryIds));
        AddParameter(command, "$selected_content_names", WriteStringList(entry.SelectedContentNames));
        AddParameter(command, "$patch_from_version", entry.PatchFromVersion);
        AddParameter(command, "$state", entry.State);
        AddParameter(command, "$status_message", entry.StatusMessage);

        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static List<string> ReadStringList(string value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split('\u001F', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static void EnsureColumn(SqliteConnection connection, string columnName, string definition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('download_history') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", columnName);

        if (Convert.ToInt32(command.ExecuteScalar()) > 0)
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE download_history ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }

    private static string WriteStringList(IEnumerable<string> values) =>
        string.Join('\u001F', values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));
}

internal sealed class DownloadHistoryEntry
{
    public int Type { get; init; }
    public string Title { get; init; } = "";
    public string DestinationDirectory { get; init; } = "";
    public List<string> LegacyUrls { get; init; } = [];
    public string? GameId { get; init; }
    public string? GameDisplayName { get; init; }
    public string? GameRegion { get; init; }
    public string? Version { get; init; }
    public string? Channel { get; init; }
    public bool DeleteChunksAfterExtraction { get; init; }
    public List<string> SelectedCategoryIds { get; init; } = [];
    public List<string> SelectedContentNames { get; init; } = [];
    public string? PatchFromVersion { get; init; }
    public int State { get; init; }
    public string? StatusMessage { get; init; }
}
