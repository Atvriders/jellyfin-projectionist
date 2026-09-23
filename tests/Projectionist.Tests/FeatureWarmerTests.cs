using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public sealed class FeatureWarmerTests : IDisposable
{
    private const int KiB = 1024;
    private readonly List<string> _files = new();
    private readonly WarmerCapturingLogger<FeatureWarmer> _log = new();

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private string TempFile(int length)
    {
        var path = Path.Combine(Path.GetTempPath(), $"projectionist-warm-{Guid.NewGuid():N}.bin");
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        _files.Add(path);
        return path;
    }

    private FeatureWarmer Warmer(WarmerRecordingFileSource files, WarmerManualTimeProvider? time = null, TimeSpan? timeout = null,
        int maxConcurrent = 2, TimeSpan? fastHead = null) =>
        new(_log, new FeatureWarmerOptions
        {
            HeadBytes = 512 * KiB,
            TailBytes = 64 * KiB,
            ProbeBytes = 16 * KiB,
            ChunkBytes = 64 * KiB,
            Time = (TimeProvider?)time ?? TimeProvider.System,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
            MaxConcurrent = maxConcurrent,
            FastHeadThreshold = fastHead ?? TimeSpan.FromMilliseconds(40),
            Files = files,
            ProtocolOf = WarmerTestSupport.ProtocolOf,
        });

    [Fact]
    public async Task WarmsHeadAndTailOfARealFile()
    {
        var path = TempFile(1024 * KiB);
        var files = new WarmerRecordingFileSource();
        var warmer = Warmer(files, fastHead: TimeSpan.Zero);

        var result = await warmer.StartWarm(path, "Alpha");

        Assert.Equal(WarmOutcome.Warmed, result.Outcome);
        Assert.Equal(512 * KiB, result.HeadBytes);
        Assert.NotNull(result.Tail);
        Assert.Null(result.Probe);
        Assert.Equal(
            new[] { new WarmRead(0, 512 * KiB), new WarmRead((1024 - 64) * KiB, 64 * KiB) },
            files.Coalesced());
        var line = Assert.Single(_log.Messages(LogLevel.Information));
        Assert.Matches(new Regex(@"^\[Projectionist\] storage warm Alpha: head 0\.5 MB in \d+ ms, tail \d+ ms$"), line);
    }

    [Fact]
    public async Task SmallFileIsReadOnce()
    {
        var path = TempFile(100 * KiB);
        var files = new WarmerRecordingFileSource();
        var result = await Warmer(files).StartWarm(path, "Tiny");

        Assert.Equal(WarmOutcome.Warmed, result.Outcome);
        Assert.Equal(new[] { new WarmRead(0, 100 * KiB) }, files.Coalesced());
        Assert.Null(result.Tail);
        Assert.Null(result.Probe);
        Assert.Contains("(whole file)", Assert.Single(_log.Messages(LogLevel.Information)));
    }

    [Fact]
    public async Task NoTailIsNotReportedAsWholeFile()
    {
        var path = TempFile(1024 * KiB);
        var files = new WarmerRecordingFileSource();
        var warmer = new FeatureWarmer(_log, new FeatureWarmerOptions
        {
            HeadBytes = 512 * KiB, TailBytes = 0, ProbeBytes = 16 * KiB, ChunkBytes = 64 * KiB,
            FastHeadThreshold = TimeSpan.Zero, Files = files,
        });

        Assert.Equal(WarmOutcome.Warmed, (await warmer.StartWarm(path, "NoTail")).Outcome);
        Assert.Equal(new[] { new WarmRead(0, 512 * KiB) }, files.Coalesced());
        Assert.DoesNotContain("whole file", Assert.Single(_log.Messages(LogLevel.Information)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FastHeadTriggersWakeProbeBeyondTheHead()
    {
        var path = TempFile(2048 * KiB);
        var time = new WarmerManualTimeProvider(); // reads take 0 ms on this clock: a page-cache hit
        var files = new WarmerRecordingFileSource();
        var result = await Warmer(files, time).StartWarm(path, "Cached");

        Assert.Equal(WarmOutcome.Warmed, result.Outcome);
        var probe = Assert.NotNull(result.Plan!.WakeProbe);
        Assert.NotNull(result.Probe);
        Assert.Equal(new[] { new WarmRead(0, 512 * KiB), probe, new WarmRead((2048 - 64) * KiB, 64 * KiB) },
            files.Coalesced().OrderBy(r => r.Offset));
        Assert.True(probe.Offset >= 512 * KiB && probe.End <= (2048 - 64) * KiB);
        Assert.Matches(new Regex(@"head 0\.5 MB in 0 ms, tail 0 ms, wake probe 0 ms$"),
            Assert.Single(_log.Messages(LogLevel.Information)));
    }

    [Fact]
    public async Task SlowHeadSkipsWakeProbe()
    {
        var path = TempFile(2048 * KiB);
        var time = new WarmerManualTimeProvider();
        var files = new WarmerRecordingFileSource { OnRead = (_, _) => time.Advance(TimeSpan.FromMilliseconds(30)) }; // 8 chunks = 240 ms
        var result = await Warmer(files, time).StartWarm(path, "Asleep");

        Assert.Equal(WarmOutcome.Warmed, result.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(240), result.Head);
        Assert.Null(result.Probe);
        Assert.Equal(2, files.Coalesced().Count);
        Assert.Matches(new Regex(@"head 0\.5 MB in 240 ms, tail 30 ms$"), Assert.Single(_log.Messages(LogLevel.Information)));
    }

    [Fact]
    public async Task SamePathIsDedupedForTwoMinutes()
    {
        var path = TempFile(512 * KiB);
        var other = TempFile(512 * KiB);
        var time = new WarmerManualTimeProvider();
        var files = new WarmerRecordingFileSource();
        var warmer = Warmer(files, time);

        Assert.Equal(WarmOutcome.Warmed, (await warmer.StartWarm(path, "A")).Outcome);
        var readsAfterFirst = files.ReadCount;

        time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(WarmOutcome.Deduplicated, (await warmer.StartWarm(path, "A")).Outcome);
        Assert.Equal(readsAfterFirst, files.ReadCount); // no I/O for a duplicate
        Assert.Equal(WarmOutcome.Warmed, (await warmer.StartWarm(other, "B")).Outcome);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(WarmOutcome.Warmed, (await warmer.StartWarm(path, "A")).Outcome);
    }

    [Fact]
    public async Task DedupeMapIsPrunedOncePerWindow()
    {
        var time = new WarmerManualTimeProvider();
        var warmer = Warmer(new WarmerRecordingFileSource(), time);
        var dir = Path.Combine(Path.GetTempPath(), $"projectionist-warm-missing-{Guid.NewGuid():N}");
        var tasks = new List<Task<WarmResult>>();

        for (var i = 0; i < 300; i++)
        {
            tasks.Add(warmer.StartWarm(Path.Combine(dir, $"{i}.mkv"), "M"));
        }

        Assert.Equal(300, warmer.DedupeEntryCount); // nothing has expired yet, so nothing is dropped

        time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        tasks.Add(warmer.StartWarm(Path.Combine(dir, "new.mkv"), "M"));

        Assert.Equal(1, warmer.DedupeEntryCount);
        Assert.All(await Task.WhenAll(tasks), r => Assert.Equal(WarmOutcome.Missing, r.Outcome));
    }

    [Fact]
    public async Task DuplicateWhileInFlightIsDeduped()
    {
        var path = TempFile(512 * KiB);
        using var gate = new ManualResetEventSlim();
        var files = new WarmerRecordingFileSource { OnExists = _ => gate.Wait(TimeSpan.FromSeconds(5)) };
        var warmer = Warmer(files);

        var first = warmer.StartWarm(path, "A");
        var second = await warmer.StartWarm(path, "A");
        gate.Set();

        Assert.Equal(WarmOutcome.Deduplicated, second.Outcome);
        Assert.Equal(WarmOutcome.Warmed, (await first).Outcome);
    }

    [Fact]
    public async Task SlowWarmGivesUpAtTheTimeoutAndFreesItsSlot()
    {
        var slow = TempFile(4096 * KiB);
        var fast = TempFile(128 * KiB);
        var files = new WarmerRecordingFileSource { OnRead = (p, _) => { if (p == slow) Thread.Sleep(50); } };
        var warmer = new FeatureWarmer(_log, new FeatureWarmerOptions
        {
            HeadBytes = 4096 * KiB, // 64 chunks x 50 ms = 3.2 s without the timeout
            ChunkBytes = 64 * KiB,
            Timeout = TimeSpan.FromMilliseconds(300),
            MaxConcurrent = 1,
            Files = files,
        });

        var sw = Stopwatch.StartNew();
        var result = await warmer.StartWarm(slow, "Slow");
        sw.Stop();

        Assert.Equal(WarmOutcome.TimedOut, result.Outcome);
        Assert.InRange(sw.ElapsedMilliseconds, 250, 2000);
        Assert.Contains(_log.Messages(LogLevel.Information), m => m.Contains("storage warm Slow: gave up after", StringComparison.Ordinal));
        Assert.Equal(WarmOutcome.Warmed, (await warmer.StartWarm(fast, "Fast")).Outcome); // the one slot came back
    }

    [Fact]
    public async Task ConcurrencyCapQueuesAndTimesOutExtraWarms()
    {
        var a = TempFile(128 * KiB);
        var b = TempFile(128 * KiB);
        using var gate = new ManualResetEventSlim();
        var files = new WarmerRecordingFileSource { OnExists = p => { if (p == a) gate.Wait(TimeSpan.FromSeconds(5)); } };
        var warmer = Warmer(files, timeout: TimeSpan.FromMilliseconds(300), maxConcurrent: 1);

        var first = warmer.StartWarm(a, "A");
        await files.WaitForExists(a);
        var second = await warmer.StartWarm(b, "B");

        Assert.Equal(WarmOutcome.TimedOut, second.Outcome);
        Assert.DoesNotContain(files.Reads, r => r.Path == b); // never got a slot, never read
        gate.Set();
        Assert.Equal(WarmOutcome.TimedOut, (await first).Outcome); // its own 300 ms budget ran out while blocked
    }

    [Fact]
    public void WarmInBackgroundReturnsImmediately()
    {
        var path = TempFile(128 * KiB);
        using var gate = new ManualResetEventSlim();
        var files = new WarmerRecordingFileSource { OnExists = _ => gate.Wait(TimeSpan.FromSeconds(5)) }; // a hung mount
        var warmer = Warmer(files);
        var movie = new Movie { Name = "Hung", Path = path };

        var sw = Stopwatch.StartNew();
        warmer.WarmInBackground(movie);
        sw.Stop();
        var blockedWhenReturned = !gate.IsSet;
        gate.Set();

        Assert.True(blockedWhenReturned);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 1000);
    }

    [Fact]
    public async Task WarmInBackgroundWarmsAMovieEndToEnd()
    {
        var path = TempFile(512 * KiB);
        var files = new WarmerRecordingFileSource();
        var warmer = Warmer(files);

        warmer.WarmInBackground(new Movie { Name = "Real", Path = path });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_log.Messages(LogLevel.Information).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.StartsWith("[Projectionist] storage warm Real: head", Assert.Single(_log.Messages(LogLevel.Information)));
    }

    [Fact]
    public async Task NeverThrows()
    {
        var files = new WarmerRecordingFileSource { OnOpen = _ => throw new UnauthorizedAccessException("nope") };
        var warmer = Warmer(files);
        var path = TempFile(64 * KiB);

        warmer.WarmInBackground(null!);
        warmer.WarmInBackground(new Movie { Name = "No path" });
        // Server-wired protocol lookup outside a server (static IMediaSourceManager may be unset).
        new FeatureWarmer(_log).WarmInBackground(new Movie { Name = "Unwired", Path = path + ".unwired" });
        Assert.Equal(WarmOutcome.Failed, (await warmer.StartWarm(path, "Locked")).Outcome);
        Assert.Equal(WarmOutcome.Missing, (await warmer.StartWarm(path + ".gone", "Gone")).Outcome);
        Assert.Empty(_log.Messages(LogLevel.Information));
    }

    [Fact]
    public async Task ProductionDefaultsWarmASparseFeature()
    {
        // 100 MB sparse file: exercises the real 32 MB head / 8 MB tail plan without disk usage.
        var path = Path.Combine(Path.GetTempPath(), $"projectionist-warm-{Guid.NewGuid():N}.bin");
        _files.Add(path);
        using (var fs = File.Create(path))
        {
            fs.SetLength(100L << 20);
        }

        var files = new WarmerRecordingFileSource();
        var result = await new FeatureWarmer(_log, new FeatureWarmerOptions { Files = files, FastHeadThreshold = TimeSpan.Zero })
            .StartWarm(path, "Sparse");

        Assert.Equal(WarmOutcome.Warmed, result.Outcome);
        Assert.Equal(new[] { new WarmRead(0, 32L << 20), new WarmRead(92L << 20, 8L << 20) }, files.Coalesced());
        Assert.Matches(new Regex(@"storage warm Sparse: head 32 MB in \d+ ms, tail \d+ ms$"),
            Assert.Single(_log.Messages(LogLevel.Information)));
    }
}

public class FeatureWarmerResolveTests
{
    [Fact]
    public void PlainLocalMovieResolvesToItsPath()
    {
        Assert.Equal("/media/movies/A.mkv", FeatureWarmer.ResolveWarmPath(new Movie { Path = "/media/movies/A.mkv" }, out _, WarmerTestSupport.ProtocolOf));
    }

    [Fact]
    public void StackedMovieWarmsPartOne()
    {
        var movie = new Movie { Path = "/m/A-cd1.avi", AdditionalParts = new[] { "/m/A-cd2.avi" } };
        Assert.Equal("/m/A-cd1.avi", FeatureWarmer.ResolveWarmPath(movie, out _, WarmerTestSupport.ProtocolOf));
    }

    [Fact]
    public void AlternateVersionsWarmTheItemsOwnPath()
    {
        var movie = new Movie { Path = "/m/A - 1080p.mkv", LocalAlternateVersions = new[] { "/m/A - 2160p.mkv" } };
        Assert.Equal("/m/A - 1080p.mkv", FeatureWarmer.ResolveWarmPath(movie, out _, WarmerTestSupport.ProtocolOf));
    }

    [Fact]
    public void IsoImageIsASingleFile()
    {
        Assert.Equal("/m/A.iso", FeatureWarmer.ResolveWarmPath(new Movie { Path = "/m/A.iso", VideoType = VideoType.Iso }, out _, WarmerTestSupport.ProtocolOf));
    }

    public static IEnumerable<object?[]> Skipped() => new[]
    {
        new object?[] { null, "not a video item" },
        new object?[] { new Folder { Path = "/m" }, "not a video item" },
        new object?[] { new Movie { Path = "/m/A.strm", IsShortcut = true, ShortcutPath = "http://x/a.mkv" }, "shortcut (.strm)" },
        new object?[] { new Movie { Path = "/m/A.disc", IsPlaceHolder = true }, "placeholder item" },
        new object?[] { new Movie { Path = "/m/A", VideoType = VideoType.Dvd }, "Dvd folder rip" },
        new object?[] { new Movie { Path = "/m/A", VideoType = VideoType.BluRay }, "BluRay folder rip" },
        new object?[] { new Movie(), "no path" },
        new object?[] { new Movie { Path = "https://cdn.example/a.mkv" }, "not a local file" },
    };

    [Theory]
    [MemberData(nameof(Skipped))]
    public void NonLocalOrUnplayableItemsAreSkipped(BaseItem? item, string reason)
    {
        Assert.Null(FeatureWarmer.ResolveWarmPath(item, out var why, WarmerTestSupport.ProtocolOf));
        Assert.Equal(reason, why);
    }
}

internal static class WarmerTestSupport
{
    /// <summary>
    /// Stands in for BaseItem.PathProtocol, which needs the server's static IMediaSourceManager
    /// (not set in unit tests, and shared global state if it were).
    /// </summary>
    public static MediaProtocol? ProtocolOf(BaseItem item) =>
        string.IsNullOrEmpty(item.Path) ? null
        : item.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaProtocol.Http
        : MediaProtocol.File;
}

internal sealed class WarmerManualTimeProvider : TimeProvider
{
    private long _ticks = TimeSpan.TicksPerDay;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

internal sealed class WarmerRecordingFileSource : IWarmFileSource
{
    private readonly ConcurrentQueue<(string Path, long Offset, int Length)> _reads = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _existsCalled = new();

    public Action<string>? OnExists { get; init; }

    public Action<string>? OnOpen { get; init; }

    public Action<string, long>? OnRead { get; init; }

    public IReadOnlyList<(string Path, long Offset, int Length)> Reads => _reads.ToList();

    public int ReadCount => _reads.Count;

    public bool Exists(string path)
    {
        _existsCalled.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        OnExists?.Invoke(path);
        return LocalWarmFileSource.Instance.Exists(path);
    }

    public Task WaitForExists(string path) =>
        _existsCalled.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
            .WaitAsync(TimeSpan.FromSeconds(5));

    public IWarmFile Open(string path)
    {
        OnOpen?.Invoke(path);
        return new RecordingFile(this, path, LocalWarmFileSource.Instance.Open(path));
    }

    /// <summary>Recorded reads merged into contiguous ranges, in the order they started.</summary>
    public List<WarmRead> Coalesced()
    {
        var ranges = new List<WarmRead>();
        foreach (var (_, offset, length) in _reads)
        {
            if (ranges.Count > 0 && ranges[^1].End == offset)
            {
                ranges[^1] = new WarmRead(ranges[^1].Offset, ranges[^1].Length + length);
            }
            else
            {
                ranges.Add(new WarmRead(offset, length));
            }
        }

        return ranges;
    }

    private sealed class RecordingFile : IWarmFile
    {
        private readonly WarmerRecordingFileSource _owner;
        private readonly string _path;
        private readonly IWarmFile _inner;

        public RecordingFile(WarmerRecordingFileSource owner, string path, IWarmFile inner)
        {
            _owner = owner;
            _path = path;
            _inner = inner;
        }

        public long Length => _inner.Length;

        public int Read(Span<byte> buffer, long offset)
        {
            _owner.OnRead?.Invoke(_path, offset);
            var n = _inner.Read(buffer, offset);
            _owner._reads.Enqueue((_path, offset, n));
            return n;
        }

        public void Dispose() => _inner.Dispose();
    }
}

internal sealed class WarmerCapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public List<string> Messages(LogLevel level) => _entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue((logLevel, formatter(state, exception)));
}
