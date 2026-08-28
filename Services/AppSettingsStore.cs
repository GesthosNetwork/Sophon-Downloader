using Microsoft.Data.Sqlite;

namespace SophonDownloader;

internal static class ConcurrencyDefaults
{
    public static int Threads => Math.Max(2, (Environment.ProcessorCount + 3) / 4);
    public static int MaxHttpConnections => Math.Max(4, Threads);
}

public sealed class AppSettings
{
    public string ThemePalette { get; set; } = "Default";
    public string ThemeMode { get; set; } = "Light";
    public string CustomAccentHex { get; set; } = "#FF7A00";
    public string FontFamily { get; set; } = "Segoe UI Variable";

    public bool UseAria2c { get; set; } = true;

    public string ProxyMode { get; set; } = "System";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 0;
    public string Dns { get; set; } = "";
    public int SpeedLimitKbps { get; set; } = 0;
    public int Threads { get; set; } = ConcurrencyDefaults.Threads;
    public int MaxHttpHandle { get; set; } = ConcurrencyDefaults.MaxHttpConnections;
    public string BackgroundImagePath { get; set; } = string.Empty;
    public bool ShowConsole { get; set; } = false;
    public string DownloadMode { get; set; } = "Parallel";
    public string LogLevel { get; set; } = "Debug";
}

public static class AppSettingsStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly string DatabasePath = Path.Combine(AppContext.BaseDirectory, "sophon.db");
    private static readonly object SyncRoot = new();
    private static AppSettings? _cachedSettings;

    public static string DatabaseFilePath => DatabasePath;

    public static AppSettings Load()
    {
        lock (SyncRoot)
        {
            if (_cachedSettings is not null)
                return Clone(_cachedSettings);

            try
            {
                EnsureDatabase();
                Dictionary<string, string> values = ReadAll();
                AppSettings settings = new()
                {
                    ThemePalette = Get(values, "ThemePalette", "Default"),
                    ThemeMode = Get(values, "ThemeMode", "Light"),
                    CustomAccentHex = NormalizeHexColor(Get(values, "CustomAccentHex", "#FF7A00"), "#FF7A00"),
                    FontFamily = Get(values, "FontFamily", "Segoe UI Variable"),
                    UseAria2c = GetBool(values, "UseAria2c", true),
                    ProxyMode = NormalizeProxyMode(Get(values, "ProxyMode", "System")),
                    ProxyHost = Get(values, "ProxyHost", ""),
                    ProxyPort = GetInt(values, "ProxyPort", 8080, 1, 65535),
                    Dns = Get(values, "Dns", ""),
                    SpeedLimitKbps = GetInt(values, "SpeedLimitKbps", 0, 0, 1024 * 1024),
                    Threads = ResolveConcurrencyValue(values, "Threads", ConcurrencyDefaults.Threads, 8, 1, 64),
                    MaxHttpHandle = ResolveConcurrencyValue(values, "MaxHttpHandle", ConcurrencyDefaults.MaxHttpConnections, 16, 1, 256),
                    BackgroundImagePath = Get(values, "BackgroundImagePath", string.Empty),
                    ShowConsole = GetBool(values, "ShowConsole", false),
                    DownloadMode = NormalizeDownloadMode(Get(values, "DownloadMode", "Parallel")),
                    LogLevel = NormalizeLogLevel(Get(values, "LogLevel", "Debug"))
                };
                _cachedSettings = settings;
                Logger.Info($"Settings initialized. Threads={settings.Threads}, MaxHttpHandle={settings.MaxHttpHandle}, ShowConsole={settings.ShowConsole}, Theme={settings.ThemeMode}/{settings.ThemePalette}.");
                return Clone(settings);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load application settings. Falling back to defaults.");
                _cachedSettings = new AppSettings();
                return Clone(_cachedSettings);
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (SyncRoot)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO AppSettings (Key, Value) VALUES ($key, $value) " +
                                  "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

            SaveValue(command, "ThemePalette", settings.ThemePalette);
            SaveValue(command, "ThemeMode", settings.ThemeMode);
            SaveValue(command, "CustomAccentHex", NormalizeHexColor(settings.CustomAccentHex, "#FF7A00"));
            SaveValue(command, "FontFamily", settings.FontFamily?.Trim() ?? "Segoe UI Variable");
            SaveValue(command, "UseAria2c", settings.UseAria2c ? "1" : "0");
            SaveValue(command, "ProxyMode", NormalizeProxyMode(settings.ProxyMode));
            SaveValue(command, "ProxyHost", settings.ProxyHost?.Trim() ?? string.Empty);
            SaveValue(command, "ProxyPort", Math.Clamp(settings.ProxyPort, 1, 65535).ToString());
            SaveValue(command, "Dns", settings.Dns?.Trim() ?? string.Empty);
            SaveValue(command, "SpeedLimitKbps", Math.Clamp(settings.SpeedLimitKbps, 0, 1024 * 1024).ToString());
            SaveValue(command, "Threads", Math.Clamp(settings.Threads, 1, 64).ToString());
            SaveValue(command, "MaxHttpHandle", Math.Clamp(settings.MaxHttpHandle, 1, 256).ToString());
            SaveValue(command, "BackgroundImagePath", settings.BackgroundImagePath?.Trim() ?? string.Empty);
            SaveValue(command, "ShowConsole", settings.ShowConsole ? "1" : "0");
            SaveValue(command, "DownloadMode", NormalizeDownloadMode(settings.DownloadMode));
            SaveValue(command, "LogLevel", NormalizeLogLevel(settings.LogLevel));

            transaction.Commit();
            _cachedSettings = Clone(settings);
        }
    }

    private static AppSettings Clone(AppSettings settings) => new()
    {
        ThemePalette = settings.ThemePalette,
        ThemeMode = settings.ThemeMode,
        CustomAccentHex = settings.CustomAccentHex,
        FontFamily = settings.FontFamily,
        UseAria2c = settings.UseAria2c,
        ProxyMode = settings.ProxyMode,
        ProxyHost = settings.ProxyHost,
        ProxyPort = settings.ProxyPort,
        Dns = settings.Dns,
        SpeedLimitKbps = settings.SpeedLimitKbps,
        Threads = settings.Threads,
        MaxHttpHandle = settings.MaxHttpHandle,
        BackgroundImagePath = settings.BackgroundImagePath,
        ShowConsole = settings.ShowConsole,
        DownloadMode = settings.DownloadMode,
        LogLevel = settings.LogLevel
    };

    private static string NormalizeHexColor(string value, string fallback)
    {
        string candidate = (value ?? string.Empty).Trim();
        if (!candidate.StartsWith("#", StringComparison.Ordinal))
            candidate = "#" + candidate;

        if (candidate.Length == 7 && candidate.Skip(1).All(Uri.IsHexDigit))
            return candidate.ToUpperInvariant();

        return fallback;
    }

    private static void SaveValue(SqliteCommand command, string key, string value)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void EnsureDatabase()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT PRIMARY KEY NOT NULL,
                Value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static Dictionary<string, string> ReadAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM AppSettings;";

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);

        return result;
    }

    private static string Get(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out string? value) ? value : fallback;

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
    {
        string value = Get(values, key, fallback ? "1" : "0");
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
    {
        return int.TryParse(Get(values, key, fallback.ToString()), out int parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private static int ResolveConcurrencyValue(Dictionary<string, string> values, string key, int autoDefault, int legacyDefault, int min, int max)
    {
        if (!values.TryGetValue(key, out string? raw) || !int.TryParse(raw, out int parsed))
            return autoDefault;

        if (parsed == legacyDefault)
            return autoDefault;

        return Math.Clamp(parsed, min, max);
    }

    private static string NormalizeProxyMode(string value) => value?.Trim() switch
    {
        "Direct" => "Direct",
        "Custom" => "Custom",
        _ => "System"
    };

    private static string NormalizeDownloadMode(string value) =>
        string.Equals(value?.Trim(), "Sequential", StringComparison.OrdinalIgnoreCase)
            ? "Sequential"
            : "Parallel";

    private static string NormalizeLogLevel(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "trace" => "Trace",
        "debug" => "Debug",
        "info" => "Info",
        "warn" => "Warn",
        "error" => "Error",
        "fatal" => "Fatal",
        _ => "Debug"
    };
}
