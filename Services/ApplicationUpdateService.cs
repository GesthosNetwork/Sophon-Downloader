using SharpCompress.Archives.SevenZip;

namespace SophonDownloader.Services;

public sealed class ApplicationUpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/GesthosNetwork/Sophon-Downloader/releases/latest";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<ApplicationUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await HttpClient.GetAsync(
            ApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken);

        JsonElement root = document.RootElement;

        string tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        string releaseName = root.GetProperty("name").GetString() ?? tagName;
        string publishedAt = root.GetProperty("published_at").GetString() ?? string.Empty;
        string? body = root.TryGetProperty("body", out JsonElement bodyElement)
            ? bodyElement.GetString()
            : null;

        if (!TryParseVersion(tagName, out Version? latestVersion) ||
            latestVersion <= new Version(App.Version))
        {
            return new ApplicationUpdateInfo(
                tagName, releaseName, publishedAt, body, null, null, false);
        }

        (string downloadUrl, string apiUrl, string fileName)? asset =
            FindAsset(root.GetProperty("assets"));

        if (asset is null)
        {
            return new ApplicationUpdateInfo(
                tagName, releaseName, publishedAt, body, null, null, true);
        }

        return new ApplicationUpdateInfo(
            tagName, releaseName, publishedAt, body, asset.Value.downloadUrl, asset.Value.fileName, true, asset.Value.apiUrl);
    }

    public async Task DownloadAndInstallAsync(
        ApplicationUpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.HasUpdate ||
            string.IsNullOrWhiteSpace(update.DownloadUrl) ||
            string.IsNullOrWhiteSpace(update.FileName))
        {
            throw new InvalidOperationException("No downloadable application update is available.");
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the application executable path.");

        string installDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("Unable to determine the application directory.");

        string executableName = Path.GetFileName(executablePath);
        string updateRoot = Path.Combine(Path.GetTempPath(), $"SophonDownloader-Update-{Guid.NewGuid():N}");
        string packagePath = Path.Combine(updateRoot, update.FileName);
        string stagingDirectory = Path.Combine(updateRoot, "staging");
        string updaterPath = Path.Combine(updateRoot, "apply-update.bat");

        Directory.CreateDirectory(updateRoot);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            await DownloadAssetAsync(update, packagePath, progress, cancellationToken);

            string extension = Path.GetExtension(packagePath);

            if (!extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsupported update package format: {extension}. Only .7z packages are supported.");
            }

            string? packageExecutable = await ExtractSevenZipToStagingAsync(
                packagePath, stagingDirectory, executableName, cancellationToken);

            if (string.IsNullOrWhiteSpace(packageExecutable))
            {
                throw new InvalidDataException($"The downloaded update package does not contain a unique application executable that can replace {executableName}.");
            }

            string stagedExecutablePath = GetSafeDestinationPath(
                stagingDirectory, packageExecutable);

            if (!File.Exists(stagedExecutablePath))
            {
                throw new InvalidDataException($"The staged update does not contain the application executable {packageExecutable}.");
            }

            File.WriteAllText(updaterPath, BuildUpdateScript());

            string commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
                ?? "cmd.exe";

            Process.Start(new ProcessStartInfo
            {
                FileName = commandProcessor,
                Arguments =
                    $"/d /c \"\"{updaterPath}\" " +
                    $"\"{Environment.ProcessId}\" " +
                    $"\"{stagingDirectory}\" " +
                    $"\"{installDirectory}\" " +
                    $"\"{executableName}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = updateRoot
            });
        }
        catch
        {
            TryDeleteDirectory(updateRoot);
            throw;
        }

        Application.Current.Shutdown();
    }

    private static async Task<string?> ExtractSevenZipToStagingAsync(
        string packagePath, string stagingDirectory, string executableName, CancellationToken cancellationToken)
    {
        List<string> paths;

        using (var archive = SevenZipArchive.Open(packagePath))
        {
            paths = archive.Entries
                .Where(entry => !entry.IsDirectory)
                .Select(entry => NormalizeArchivePath(entry.Key ?? string.Empty))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
        }

        string wrapper = DetermineWrapper(paths);
        List<string> relativePaths = paths
            .Select(path => StripWrapper(path, wrapper))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        string? packageExecutable = FindPackageExecutable(relativePaths, executableName);

        using var archiveToExtract = SevenZipArchive.Open(packagePath);

        foreach (var entry in archiveToExtract.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
                continue;

            string path = NormalizeArchivePath(entry.Key ?? string.Empty);
            string relativePath = StripWrapper(path, wrapper);

            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            string destination = GetSafeDestinationPath(stagingDirectory, relativePath);

            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)
                ?? stagingDirectory);

            await using Stream source = entry.OpenEntryStream();
            await using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, true);
            await source.CopyToAsync(target, 128 * 1024, cancellationToken);
        }

        return packageExecutable;
    }

    private static string DetermineWrapper(IReadOnlyCollection<string> paths)
    {
        string[] topLevels = paths
            .Select(path => path.Split('/')[0])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (topLevels.Length != 1)
            return string.Empty;

        string wrapper = topLevels[0];
        string prefix = wrapper + "/";

        return paths.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? wrapper
            : string.Empty;
    }

    private static string StripWrapper(string path, string wrapper)
    {
        if (string.IsNullOrWhiteSpace(wrapper))
            return path;

        string prefix = wrapper + "/";

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : path;
    }

    private static string? FindPackageExecutable(
        IEnumerable<string> paths, string installedExecutableName)
    {
        string[] executables = paths
            .Where(path =>
                Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string? exact = executables.FirstOrDefault(path =>
            Path.GetFileName(path).Equals(installedExecutableName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(exact))
            return exact;

        return executables.Length == 1
            ? executables[0]
            : null;
    }

    private static string GetSafeDestinationPath(
        string installDirectory, string relativePath)
    {
        string root = Path.GetFullPath(installDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string destination = Path.GetFullPath(
            Path.Combine(installDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The update package contains an invalid path: {relativePath}");
        }

        return destination;
    }

    private static string NormalizeArchivePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static async Task DownloadAssetAsync(
        ApplicationUpdateInfo update, string packagePath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        List<string> urls = [];

        if (!string.IsNullOrWhiteSpace(update.DownloadApiUrl))
            urls.Add(update.DownloadApiUrl);

        if (!string.IsNullOrWhiteSpace(update.DownloadUrl))
            urls.Add(update.DownloadUrl);

        if (!string.IsNullOrWhiteSpace(update.FileName))
        {
            urls.Add($"https://github.com/GesthosNetwork/Sophon-Downloader/releases/latest/download/{Uri.EscapeDataString(update.FileName)}");
        }

        Exception? lastError = null;

        foreach (string url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

                if (url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                }

                using HttpResponseMessage response = await HttpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}) while downloading the update asset.");
                    continue;
                }

                long? totalBytes = response.Content.Headers.ContentLength;
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using FileStream target = new(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);

                byte[] buffer = new byte[128 * 1024];
                long downloaded = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                    downloaded += read;

                    if (totalBytes is > 0)
                    {
                        progress?.Report(downloaded / (double)totalBytes.Value * 100d);
                    }
                }

                return;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError
            ?? new HttpRequestException("Unable to download the application update asset.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SophonDownloader", App.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return client;
    }

    private static (string downloadUrl, string apiUrl, string fileName)? FindAsset(JsonElement assets)
    {
        var candidates = assets
            .EnumerateArray().Select(asset =>
            {
                string name = asset.GetProperty("name").GetString() ?? string.Empty;
                string browserUrl = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                string apiUrl = asset.TryGetProperty("url", out JsonElement urlElement)
                    ? urlElement.GetString() ?? string.Empty
                    : string.Empty;

                return (name, browserUrl, apiUrl);
            })
            .Where(asset => asset.name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            .Where(asset => !asset.name.Contains("source", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sevenZip = candidates.FirstOrDefault(asset => asset.name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(sevenZip.browserUrl)
            ? null
            : (sevenZip.browserUrl, sevenZip.apiUrl, sevenZip.name);
    }

    private static bool TryParseVersion(string value, out Version? version)
    {
        value = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(value, out version);
    }

    private static string BuildUpdateScript() => """
setlocal EnableExtensions

set "ProcessId=%~1"
set "StagingDirectory=%~2"
set "InstallDirectory=%~3"
set "Executable=%~4"
set "Updater=%~f0"
set "Robocopy=%SystemRoot%\System32\robocopy.exe"
set "TaskList=%SystemRoot%\System32\tasklist.exe"
set "Find=%SystemRoot%\System32\find.exe"
set "Timeout=%SystemRoot%\System32\timeout.exe"

if not exist "%StagingDirectory%\%Executable%" goto failed

:wait
"%TaskList%" /FI "PID eq %ProcessId%" /NH 2>nul | "%Find%" "%ProcessId%" >nul
if not errorlevel 1 (
    "%Timeout%" /t 1 /nobreak >nul
    goto wait
)

:copy
"%Robocopy%" "%StagingDirectory%" "%InstallDirectory%" /E /COPY:DAT /DCOPY:DAT /R:10 /W:1 /XJ /NFL /NDL /NJH /NJS /NP >nul
set "CopyExitCode=%ERRORLEVEL%"

if %CopyExitCode% GEQ 8 (
    "%Timeout%" /t 1 /nobreak >nul
    goto copy
)

start "" /D "%InstallDirectory%" "%InstallDirectory%\%Executable%"

"%Timeout%" /t 2 /nobreak >nul
rmdir /S /Q "%StagingDirectory%" >nul 2>&1
for %%D in ("%StagingDirectory%\..") do rmdir /S /Q "%%~fD" >nul 2>&1

del /F /Q "%Updater%" >nul 2>&1
exit /b 0

:failed
"%Timeout%" /t 2 /nobreak >nul
exit /b 1
""";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch {}
    }
}

public sealed record ApplicationUpdateInfo(
    string Version, string Name, string PublishedAt, string? ReleaseNotes,
    string? DownloadUrl, string? FileName, bool HasUpdate,
    string? DownloadApiUrl = null);
