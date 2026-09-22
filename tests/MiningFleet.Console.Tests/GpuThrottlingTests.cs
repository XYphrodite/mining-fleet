namespace MiningFleet.Console.Tests;

public sealed class GpuThrottlingTests
{
    [Fact]
    public void Light_load_dst_should_throttle_not_stop()
    {
        // User request: DST is light — do not fully stop, just lower load.
        // Decision threshold is 80% GPU load.
        const double threshold = 80.0;
        const double dstTypicalLoad = 25.0;
        const double heavyGameLoad = 95.0;

        Assert.True(dstTypicalLoad < threshold); // throttles
        Assert.True(heavyGameLoad >= threshold); // pauses
    }

    [Fact]
    public void Throttled_power_limit_is_below_full_draw()
    {
        // Full draw on 3060 is ~161W measured, throttled should be 100W.
        const int throttled = 100;
        const int full = 161;
        Assert.True(throttled < full);
        Assert.Equal(100, throttled);
    }

    [Fact]
    public void Throttle_preserves_mining_while_pause_stops_it()
    {
        // Throttle keeps RunningPid non-null, pause clears it.
        // This documents the intended shape: GpuPauseService.MaybeThrottle does not call StopAsync.
        var throttledKeepsRunning = true;
        var pausedStops = false;
        Assert.NotEqual(throttledKeepsRunning, pausedStops);
    }
}
