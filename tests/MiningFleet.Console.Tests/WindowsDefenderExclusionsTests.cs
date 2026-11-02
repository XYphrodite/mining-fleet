using MiningFleet.Agent;

namespace MiningFleet.Console.Tests;

public sealed class WindowsDefenderExclusionsTests
{
    [Fact]
    public void BuildAddProcessScript_uses_full_executable_path_and_removes_legacy_bare_name()
    {
        using var dir = new TempDirectory();
        var exe = Path.Combine(dir.Path, "xmrig-6.26.0", "xmrig.exe");

        var script = WindowsDefenderExclusions.BuildAddProcessScript(exe);

        Assert.Contains($"Remove-MpPreference -ExclusionProcess 'xmrig.exe'", script);
        Assert.Contains($"Add-MpPreference -ExclusionProcess '{Path.GetFullPath(exe)}'", script);
    }

    [Fact]
    public void BuildAddPathScript_escapes_single_quotes()
    {
        using var dir = new TempDirectory();
        var quotedDir = Path.Combine(dir.Path, "miner's-dir");

        var script = WindowsDefenderExclusions.BuildAddPathScript(quotedDir);

        Assert.Contains("miner''s-dir", script);
        Assert.DoesNotContain("-ExclusionProcess", script);
    }

    [Fact]
    public void BuildRemoveUnderPathScript_targets_root_and_children()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.Path, "xmrig") + Path.DirectorySeparatorChar;

        var script = WindowsDefenderExclusions.BuildRemoveUnderPathScript(target);
        var expectedRoot = Path.Combine(dir.Path, "xmrig");
        var expectedPrefix = expectedRoot + Path.DirectorySeparatorChar;

        Assert.Contains($"$r = '{expectedRoot}'", script);
        Assert.Contains($"$pfx = '{expectedPrefix}'", script);
        Assert.Contains("Remove-MpPreference -ExclusionPath", script);
        Assert.Contains("Remove-MpPreference -ExclusionProcess", script);
    }
}
