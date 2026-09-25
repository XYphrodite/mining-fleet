using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Reads the governor's small, atomically replaced status file. An expired heartbeat must not
/// claim that a game is still running or that limits are still being maintained.
/// </summary>
public static class GameMiningStatusReader
{
    public const string FileName = "game-mining-status.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static GameMiningStatusDto? Read(string directory, DateTimeOffset now)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, FileName), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 8192) return null;
            var status = JsonSerializer.Deserialize<GameMiningStatusDto>(stream, JsonOptions);
            if (status is not { Active: true } || string.IsNullOrWhiteSpace(status.GameName) ||
                status.GameName.Length > 128 || status.ObservedAt > now.AddSeconds(5) ||
                now - status.ObservedAt > TimeSpan.FromSeconds(90)) return null;

            return status.Fps is { } fps && (!double.IsFinite(fps) || fps < 0)
                ? status with { Fps = null }
                : status;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing helper, partial/corrupt telemetry or a replacement in progress cannot
            // make the node's otherwise healthy /status request fail.
            return null;
        }
    }
}
