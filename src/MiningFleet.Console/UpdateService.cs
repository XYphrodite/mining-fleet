using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace MiningFleet.Console;

/// <summary>A release newer than what is running, with the asset that fits this machine.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string DownloadUrl, long SizeBytes, string? Notes);

/// <summary>
/// Self-update for the operator console: asks GitHub for the latest release, downloads the
/// asset for this platform, and swaps the files in place.
///
/// Replacing a running executable works because Windows allows renaming the image of a live
/// process even though it cannot be deleted: the old file is moved aside and cleaned up on
/// the next start.
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>Files displaced by a previous update, removed on the next run.</summary>
    public const string BackupSuffix = ".old";

    private readonly UpdateConfig _config;
    private readonly HttpClient _http;

    public UpdateService(UpdateConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // The GitHub API rejects requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"mining-fleet/{CurrentVersion}");
        if (!string.IsNullOrWhiteSpace(config.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>Returns the newer release, or null when this build is already current.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.Repository))
            throw new InvalidOperationException("update.repository is not set in fleet.json (expected \"owner/name\").");

        JsonDocument? doc = null;
        string? lastMissing = null;
        foreach (var repo in MiningFleet.Contracts.ReleaseAssets.RepositoriesToTry(_config.Repository))
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                lastMissing = repo;
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            break;
        }

        if (doc is null)
            throw new InvalidOperationException($"No published release found for {lastMissing ?? _config.Repository}.");

        using (doc)
        {
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var released))
                throw new InvalidOperationException($"Release tag '{tag}' is not a version this console can compare.");

            if (released <= CurrentVersion) return null;

            var asset = FindAsset(doc.RootElement);
            if (asset is null)
                throw new InvalidOperationException($"Release {tag} carries no '{string.Join("' or '", AssetNames)}'.");

            var notes = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : null;
            return asset with { Version = released, Tag = tag, Notes = notes };
        }
    }

    /// <summary>
    /// Downloads and installs an update. <paramref name="onProgress"/> reports bytes received
    /// and the total when the server declares one.
    /// </summary>
    public async Task<string> ApplyAsync(UpdateInfo update, Action<long, long?> onProgress, CancellationToken ct)
    {
        var archive = Path.Combine(Path.GetTempPath(), $"mining-fleet-{update.Tag}-{Guid.NewGuid():N}.zip");
        var unpacked = Path.Combine(Path.GetTempPath(), $"mining-fleet-{Guid.NewGuid():N}");

        try
        {
            await DownloadAsync(update.DownloadUrl, archive, onProgress, ct);

            Directory.CreateDirectory(unpacked);
            ZipFile.ExtractToDirectory(archive, unpacked, overwriteFiles: true);

            var replaced = SwapIntoPlace(unpacked, InstallDirectory);
            return $"Updated to {update.Tag} ({replaced} file(s)). Restart mining-fleet to run the new version.";
        }
        finally
        {
            TryDelete(archive);
            TryDeleteDirectory(unpacked);
        }
    }

    private async Task DownloadAsync(string url, string destination, Action<long, long?> onProgress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Release assets are served as octet-stream; without this GitHub returns JSON metadata.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            onProgress(received, total);
        }
    }

    /// <summary>
    /// Copies the unpacked payload over the installation, moving any file that is in use out
    /// of the way first. Returns how many files were written.
    /// </summary>
    private static int SwapIntoPlace(string source, string target)
    {
        var written = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination))
            {
                var backup = destination + BackupSuffix;
                TryDelete(backup);
                // Renaming works even for the running executable; deleting it would not.
                File.Move(destination, backup);
            }

            File.Copy(file, destination, overwrite: true);
            written++;
        }

        return written;
    }

    /// <summary>Removes files displaced by an earlier update. Safe to call on every start.</summary>
    public static void CleanUpPreviousUpdate()
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(InstallDirectory, "*" + BackupSuffix, SearchOption.AllDirectories))
                TryDelete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are harmless; never let cleanup stop the console from starting.
        }
    }

    /// <summary>Preferred console zip, e.g. mining-fleet-win-x64.zip. See <see cref="AssetNames"/>.</summary>
    public static string AssetName => AssetNames[0];

    /// <summary>
    /// Console zip names, new then legacy. Matched in full rather than by fragment: a release
    /// also carries the agent zip, and a substring match would unpack it over the console.
    /// </summary>
    public static IReadOnlyList<string> AssetNames => MiningFleet.Contracts.ReleaseAssets.ConsoleZipNames;

    public static string? PickAsset(IEnumerable<string?> available) =>
        MiningFleet.Contracts.ReleaseAssets.Pick(AssetNames, available);

    private static UpdateInfo? FindAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<(string Name, string Url, long Size)>();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null) continue;
            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            names.Add((name, url, size));
        }

        var picked = PickAsset(names.Select(a => a.Name));
        if (picked is null) return null;

        var match = names.First(a => a.Name.Equals(picked, StringComparison.OrdinalIgnoreCase));
        return new UpdateInfo(new Version(0, 0), "", match.Name, match.Url, match.Size, null);
    }

    private static bool TryParseVersion(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out version!);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _http.Dispose();
}
