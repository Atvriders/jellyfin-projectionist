using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class FeatureWarmerPlannerTests
{
    private const long MiB = 1L << 20;
    private const long H = StorageWarmPlanner.DefaultHeadBytes;
    private const long T = StorageWarmPlanner.DefaultTailBytes;
    private const long P = StorageWarmPlanner.DefaultProbeBytes;

    [Fact]
    public void DefaultsAreHead32TailEightProbeOne()
    {
        Assert.Equal(32 * MiB, H);
        Assert.Equal(8 * MiB, T);
        Assert.Equal(1 * MiB, P);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EmptyOrInvalidLengthPlansNothing(long length)
    {
        var plan = StorageWarmPlanner.Plan(length);
        Assert.Empty(plan.Reads);
        Assert.Null(plan.WakeProbe);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(5 * MiB)]
    [InlineData(H)]
    [InlineData(H + T)]
    public void TinyFileIsReadOnceWithoutProbe(long length)
    {
        var plan = StorageWarmPlanner.Plan(length);
        var read = Assert.Single(plan.Reads);
        Assert.Equal(new WarmRead(0, length), read);
        Assert.Null(plan.WakeProbe);
    }

    [Fact]
    public void MediumFileGetsHeadTailAndAProbeInTheGap()
    {
        var length = 500 * MiB;
        var plan = StorageWarmPlanner.Plan(length);

        Assert.Equal(new[] { new WarmRead(0, H), new WarmRead(length - T, T) }, plan.Reads);
        var probe = Assert.NotNull(plan.WakeProbe);
        Assert.Equal(P, probe.Length);
        Assert.True(probe.Offset >= H && probe.End <= length - T);
        Assert.Equal(0, probe.Offset % 4096);
        Assert.InRange(probe.Offset, (length / 2) - P, length / 2);
    }

    [Fact]
    public void HugeFileProbesTheMiddle()
    {
        var length = 80L * 1024 * MiB + 12345; // an 80 GB remux, odd length
        var plan = StorageWarmPlanner.Plan(length);

        Assert.Equal(2, plan.Reads.Count);
        Assert.Equal(new WarmRead(0, H), plan.Reads[0]);
        Assert.Equal(new WarmRead(length - T, T), plan.Reads[1]);
        Assert.Equal(length, plan.Reads[1].End);
        var probe = Assert.NotNull(plan.WakeProbe);
        Assert.Equal(P, probe.Length);
        Assert.InRange(probe.Offset, (length / 2) - P, length / 2);
    }

    [Fact]
    public void GapSmallerThanProbeIsProbedExactly()
    {
        var length = H + T + 10;
        var plan = StorageWarmPlanner.Plan(length);

        Assert.Equal(2, plan.Reads.Count);
        Assert.Equal(new WarmRead(H, 10), plan.WakeProbe);
    }

    [Fact]
    public void ZeroTailOrProbeDisablesThem()
    {
        var noTail = StorageWarmPlanner.Plan(100 * MiB, H, 0, P);
        Assert.Equal(new[] { new WarmRead(0, H) }, noTail.Reads);
        Assert.NotNull(noTail.WakeProbe);

        var noProbe = StorageWarmPlanner.Plan(100 * MiB, H, T, 0);
        Assert.Equal(2, noProbe.Reads.Count);
        Assert.Null(noProbe.WakeProbe);
    }

    public static IEnumerable<object[]> Lengths()
    {
        var fixedLengths = new[]
        {
            1L, 2, 4095, 4096, 4097, MiB, H - 1, H, H + 1, H + T - 1, H + T, H + T + 1, H + T + 4096,
            H + T + P - 1, H + T + P, H + T + P + 1, (2 * H) + T, 613_597_467, 429_052_826, 622_084_516,
            long.MaxValue / 4,
        };
        foreach (var l in fixedLengths)
        {
            yield return new object[] { l };
        }

        var rng = new Random(1234);
        for (var i = 0; i < 200; i++)
        {
            yield return new object[] { 1 + (long)(rng.NextDouble() * 200L * 1024 * MiB) };
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void ReadsAreInRangeAndNeverOverlap(long length)
    {
        var plan = StorageWarmPlanner.Plan(length);
        var all = plan.Reads.Concat(plan.WakeProbe is { } p ? new[] { p } : Array.Empty<WarmRead>())
            .OrderBy(r => r.Offset)
            .ToList();

        Assert.NotEmpty(plan.Reads);
        Assert.Equal(0, plan.Reads[0].Offset);
        foreach (var r in all)
        {
            Assert.True(r.Length > 0, $"empty read {r}");
            Assert.True(r.Offset >= 0 && r.End <= length, $"read {r} outside file of {length}");
        }

        for (var i = 1; i < all.Count; i++)
        {
            Assert.True(all[i - 1].End <= all[i].Offset, $"{all[i - 1]} overlaps {all[i]}");
        }

        Assert.True(all.Sum(r => r.Length) <= H + T + P);
        if (plan.Reads.Count > 1)
        {
            Assert.Equal(length, plan.Reads[^1].End); // the tail ends at EOF
        }
        else
        {
            Assert.Null(plan.WakeProbe);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(39, true)]
    [InlineData(40, false)]
    [InlineData(5000, false)]
    public void ProbeOnlyWhenHeadIsFast(int headMs, bool expected)
    {
        Assert.Equal(expected, StorageWarmPlanner.ShouldProbe(TimeSpan.FromMilliseconds(headMs), TimeSpan.FromMilliseconds(40)));
    }
}
