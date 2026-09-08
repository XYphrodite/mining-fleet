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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"xmrig-fleet/{CurrentVersion}");
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

        var url = $"https://api.github.com/repos/{_config.Repository.Trim('/')}/releases/latest";
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"No published release found for {_config.Repository}.");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseVersion(tag, out var released))
            throw new InvalidOperationException($"Release tag '{tag}' is not a version this console can compare.");

        if (released <= CurrentVersion) return null;

        var asset = FindAsset(doc.RootElement);
        if (asset is null)
            throw new InvalidOperationException($"Release {tag} carries no '{AssetName}'.");

        var notes = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : null;
        return asset with { Version = released, Tag = tag, Notes = notes };
    }

    /// <summary>
    /// Downloads and installs an update. <paramref name="onProgress"/> reports bytes received
    /// and the total when the server declares one.
    /// </summary>
    public async Task<string> ApplyAsync(UpdateInfo update, Action<long, long?> onProgress, CancellationToken ct)
    {
        var archive = Path.Combine(Path.GetTempPath(), $"xmrig-fleet-{update.Tag}-{Guid.NewGuid():N}.zip");
        var unpacked = Path.Combine(Path.GetTempPath(), $"xmrig-fleet-{Guid.NewGuid():N}");

        try
        {
            await DownloadAsync(update.DownloadUrl, archive, onProgress, ct);

            Directory.CreateDirectory(unpacked);
            ZipFile.ExtractToDirectory(archive, unpacked, overwriteFiles: true);

            var replaced = SwapIntoPlace(unpacked, InstallDirectory);
            return $"Updated to {update.Tag} ({replaced} file(s)). Restart xmrig-fleet to run the new version.";
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

    /// <summary>
    /// The one asset this console may install, e.g. xmrig-fleet-win-x64.zip.
    ///
    /// Matched in full rather than by fragment: a release also carries
    /// xmrig-fleet-agent-win-x64.zip, and a substring match on the platform would happily
    /// unpack the node agent over the console.
    /// </summary>
    public static string AssetName
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                var other => other.ToString().ToLowerInvariant(),
            };
            return $"xmrig-fleet-{os}-{arch}.zip";
        }
    }

    private static UpdateInfo? FindAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null) continue;

            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            return new UpdateInfo(new Version(0, 0), "", name, url, size, null);
        }

        return null;
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
