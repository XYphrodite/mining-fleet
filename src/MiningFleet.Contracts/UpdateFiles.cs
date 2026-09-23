namespace MiningFleet.Contracts;

/// <summary>Shared file checks for the fleet self-update wiring.</summary>
public static class UpdateFiles
{
    /// <summary>
    /// A service binary cannot answer a <c>--help</c> probe, so its staged copy is
    /// identified by the executable header instead. Anything else is refused before
    /// it can take the place of the running binary.
    /// </summary>
    public static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>First candidate present in the directory, or the preferred name.</summary>
    public static string FirstExisting(string directory, IEnumerable<string> candidates, string fallback)
    {
        foreach (var name in candidates)
        {
            if (!string.IsNullOrWhiteSpace(name) && File.Exists(Path.Combine(directory, name)))
                return Path.Combine(directory, name);
        }

        return Path.Combine(directory, fallback);
    }
}
