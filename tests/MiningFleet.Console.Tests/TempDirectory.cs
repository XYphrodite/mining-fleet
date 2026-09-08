namespace MiningFleet.Console.Tests;

/// <summary>
/// A scratch directory for tests that drive a <see cref="MiningFleet.Agent.MinerConfigStore"/>,
/// which is only meaningful against a real miner.json on disk.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("mining-fleet-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
