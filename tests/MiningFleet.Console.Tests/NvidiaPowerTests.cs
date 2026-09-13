using MiningFleet.Agent;

namespace MiningFleet.Console.Tests;

public sealed class NvidiaPowerTests
{
    private const string Samples = """
        GPU 00000000:01:00.0
            GPU Power Readings
                Average Power Draw : N/A
                Instantaneous Power Draw : N/A
                Current Power Limit : 115.00 W
            Power Samples
                Duration : 2.45 sec
                Number of Samples : 119
                Max : 109.24 W
                Min : 98.01 W
                Avg : 100.68 W
            GPU Memory Power Readings
                Average Power Draw : 7.00 W
            Module Power Readings
                Average Power Draw : 999.00 W
        """;

    [Fact]
    public void MissingDrawUsesSampleAverageNotLimitPeakMemoryOrModulePower()
    {
        var result = NvidiaPowerReader.Parse("00000000:01:00.0, NVIDIA GeForce RTX 4060, [N/A]", Samples);
        Assert.Equal(100.68, result["NVIDIA GeForce RTX 4060"]);
    }

    [Fact]
    public void DirectDrawWinsOverSampleAverage()
    {
        var result = NvidiaPowerReader.Parse("00000000:01:00.0, NVIDIA GeForce RTX 4060, 101.2", Samples);
        Assert.Equal(101.2, result["NVIDIA GeForce RTX 4060"]);
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("NaN")]
    [InlineData("-1")]
    [InlineData("Infinity")]
    public void UnsupportedValuesStayUnknown(string value)
    {
        Assert.Empty(NvidiaPowerReader.Parse($"00000000:01:00.0, RTX, {value}", ""));
    }

    [Fact]
    public void SamplesAreMatchedByPciBusAndDuplicateModelsAreNotGuessed()
    {
        Assert.Empty(NvidiaPowerReader.Parse("00000000:02:00.0, RTX, N/A", Samples));
        Assert.Empty(NvidiaPowerReader.Parse("00000000:01:00.0, RTX, 100\n00000000:02:00.0, RTX, 80", ""));
    }
}
