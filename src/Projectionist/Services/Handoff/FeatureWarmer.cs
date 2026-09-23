using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>
/// Storage warm: while the prerolls play, read the feature file's head and tail so a
/// spun-down disk, NAS share or FUSE/rclone mount is awake (and the bytes ffmpeg and the
/// player read first are in the page cache) before the player opens the feature.
/// Triggered from <c>PrerollIntroProvider.GetIntros</c>, so it runs for every client that
/// calls /Intros. Local files only; everything happens off the caller's thread.
/// </summary>
public sealed class FeatureWarmer : IFeatureWarmer
{
    private readonly ILogger<FeatureWarmer> _logger;
    private readonly FeatureWarmerOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly object _recentLock = new();
    /// <summary>Path → timestamp (TimeProvider ticks) of the last warm started for it.</summary>
    private readonly Dictionary<string, long> _recent = new(StringComparer.Ordinal);
    private long _lastPrune;

    /// <summary>Prune expired dedupe entries once the map grows past this.</summary>
    private const int RecentPruneThreshold = 256;

    public FeatureWarmer(ILogger<FeatureWarmer> logger)
        : this(logger, new FeatureWarmerOptions())
    {
    }

    internal FeatureWarmer(ILogger<FeatureWarmer> logger, FeatureWarmerOptions options)
    {
        _logger = logger;
        _options = options;
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
    }

    public void WarmInBackground(BaseItem feature)
    {
        try
        {
            _ = StartWarm(feature);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] storage warm could not start for {Item}", feature?.Name);
        }
    }

    /// <summary>
    /// Resolves and dedupes on the caller's thread (no I/O), then warms on the thread pool.
    /// The returned task never faults; tests await it, production discards it.
    /// </summary>
    internal Task<WarmResult> StartWarm(BaseItem? feature)
    {
        var path = ResolveWarmPath(feature, out var reason, _options.ProtocolOf);
        if (path is null)
        {
            _logger.LogDebug("[Projectionist] storage warm skipped for {Item}: {Reason}", feature?.Name, reason);
            return Task.FromResult(WarmResult.Of(WarmOutcome.Skipped));
        }

        return StartWarm(path, feature!.Name ?? path);
    }

    internal Task<WarmResult> StartWarm(string path, string name)
    {
        if (!TryClaim(path))
        {
            _logger.LogDebug("[Projectionist] storage warm skipped for {Item}: warmed within the last {Minutes} min",
                name, _options.DedupeWindow.TotalMinutes);
            return Task.FromResult(WarmResult.Of(WarmOutcome.Deduplicated));
        }

        return Task.Run(() => RunAsync(path, name));
    }

    /// <summary>
    /// The file to warm for <paramref name="item"/>, or null (with a reason) when it is not a
    /// plain local video file. Reads item properties only; the File.Exists check happens later,
    /// off the caller's thread. <paramref name="protocolOf"/> defaults to <see cref="BaseItem.PathProtocol"/>.
    /// </summary>
    internal static string? ResolveWarmPath(BaseItem? item, out string reason, Func<BaseItem, MediaProtocol?>? protocolOf = null)
    {
        if (item is not Video video)
        {
            reason = "not a video item";
            return null;
        }

        if (video.IsShortcut)
        {
            reason = "shortcut (.strm)";
            return null;
        }

        if (video.IsPlaceHolder)
        {
            reason = "placeholder item";
            return null;
        }

        // Dvd/BluRay items point at a folder rip; an .iso is a single file and still worth waking.
        if (video.VideoType is VideoType.Dvd or VideoType.BluRay)
        {
            reason = $"{video.VideoType} folder rip";
            return null;
        }

        // For a multi-part (stacked) item Path is part 1; for alternate versions it is the
        // item's own version, which is what clients play by default.
        var path = video.Path;
        if (string.IsNullOrEmpty(path))
        {
            reason = "no path";
            return null;
        }

        if ((protocolOf is null ? video.PathProtocol : protocolOf(video)) != MediaProtocol.File)
        {
            reason = "not a local file";
            return null;
        }

        reason = string.Empty;
        return path;
    }

    internal int DedupeEntryCount
    {
        get
        {
            lock (_recentLock)
            {
                return _recent.Count;
            }
        }
    }

    private bool TryClaim(string path)
    {
        var now = _options.Time.GetTimestamp();
        lock (_recentLock)
        {
            if (_recent.TryGetValue(path, out var last) &&
                _options.Time.GetElapsedTime(last, now) < _options.DedupeWindow)
            {
                return false;
            }

            _recent[path] = now;

            // At most one O(n) sweep per dedupe window, however many distinct features arrive.
            if (_recent.Count > RecentPruneThreshold &&
                _options.Time.GetElapsedTime(_lastPrune, now) >= _options.DedupeWindow)
            {
                _lastPrune = now;
                foreach (var stale in _recent
                    .Where(kv => _options.Time.GetElapsedTime(kv.Value, now) >= _options.DedupeWindow)
                    .Select(kv => kv.Key)
                    .ToList())
                {
                    _recent.Remove(stale);
                }
            }

            return true;
        }
    }

    private async Task<WarmResult> RunAsync(string path, string name)
    {
        var started = _options.Time.GetTimestamp();
        var acquired = false;
        try
        {
            using var cts = new CancellationTokenSource(_options.Timeout, _options.Time);
            try
            {
                // The cap bounds threads stuck in a hung mount; a queued warm gives up with the timeout.
                await _slots.WaitAsync(cts.Token).ConfigureAwait(false);
                acquired = true;

                var result = WarmFile(path, cts.Token);
                Log(name, result);
                return result;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _logger.LogInformation("[Projectionist] storage warm {Name}: gave up after {Ms} ms (timeout)",
                    name, (long)_options.Time.GetElapsedTime(started).TotalMilliseconds);
                return WarmResult.Of(WarmOutcome.TimedOut);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] storage warm {Name} failed", name);
            return WarmResult.Of(WarmOutcome.Failed);
        }
        finally
        {
            if (acquired)
            {
                _slots.Release();
            }
        }
    }

    private WarmResult WarmFile(string path, CancellationToken ct)
    {
        var files = _options.Files;
        if (!files.Exists(path))
        {
            return WarmResult.Of(WarmOutcome.Missing);
        }

        var time = _options.Time;
        var chunk = Math.Max(4096, _options.ChunkBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(chunk);
        try
        {
            // Head timing includes the open: on FUSE/rclone the open itself can be the slow part.
            var t0 = time.GetTimestamp();
            using var file = files.Open(path);
            var plan = StorageWarmPlanner.Plan(file.Length, _options.HeadBytes, _options.TailBytes, _options.ProbeBytes);
            if (plan.Reads.Count == 0)
            {
                return WarmResult.Of(WarmOutcome.Skipped); // empty file
            }

            var headBytes = ReadRange(file, plan.Reads[0], buffer.AsSpan(0, chunk), ct);
            var head = time.GetElapsedTime(t0);

            TimeSpan? tail = null;
            if (plan.Reads.Count > 1)
            {
                var t1 = time.GetTimestamp();
                ReadRange(file, plan.Reads[1], buffer.AsSpan(0, chunk), ct);
                tail = time.GetElapsedTime(t1);
            }

            // A head that fast came from the page cache: the disk may still be asleep and would
            // stall playback later, past the cached region. Touch one block beyond the head.
            TimeSpan? probe = null;
            if (plan.WakeProbe is { } wake && StorageWarmPlanner.ShouldProbe(head, _options.FastHeadThreshold))
            {
                var t2 = time.GetTimestamp();
                ReadRange(file, wake, buffer.AsSpan(0, chunk), ct);
                probe = time.GetElapsedTime(t2);
            }

            return new WarmResult(WarmOutcome.Warmed, headBytes, head, tail, probe, plan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long ReadRange(IWarmFile file, WarmRead range, Span<byte> buffer, CancellationToken ct)
    {
        long done = 0;
        while (done < range.Length)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, range.Length - done);
            var n = file.Read(buffer[..want], range.Offset + done);
            if (n <= 0)
            {
                break; // file shrank under us
            }

            done += n;
        }

        return done;
    }

    private void Log(string name, WarmResult r)
    {
        if (r.Outcome != WarmOutcome.Warmed)
        {
            _logger.LogDebug("[Projectionist] storage warm skipped for {Item}: {Outcome}", name, r.Outcome);
            return;
        }

        var mb = Math.Round(r.HeadBytes / (double)(1 << 20), 1);
        var headMs = (long)r.Head.TotalMilliseconds;
        var tailMs = (long)(r.Tail?.TotalMilliseconds ?? 0);
        if (r.Plan is { Reads.Count: 1, WakeProbe: null })
        {
            _logger.LogInformation("[Projectionist] storage warm {Name}: head {HeadMb} MB in {HeadMs} ms (whole file)",
                name, mb, headMs);
        }
        else if (r.Probe is null)
        {
            _logger.LogInformation("[Projectionist] storage warm {Name}: head {HeadMb} MB in {HeadMs} ms, tail {TailMs} ms",
                name, mb, headMs, tailMs);
        }
        else
        {
            _logger.LogInformation(
                "[Projectionist] storage warm {Name}: head {HeadMb} MB in {HeadMs} ms, tail {TailMs} ms, wake probe {ProbeMs} ms",
                name, mb, headMs, tailMs, (long)r.Probe.Value.TotalMilliseconds);
        }
    }
}

/// <summary>Knobs for <see cref="FeatureWarmer"/>; the defaults are the production values.</summary>
internal sealed class FeatureWarmerOptions
{
    /// <summary>A path warmed less than this long ago is not warmed again.</summary>
    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Overall budget per warm, including the wait for a free slot.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Warms running at once, across all features.</summary>
    public int MaxConcurrent { get; init; } = 2;

    /// <summary>A head read faster than this is treated as a page-cache hit (see the wake probe).</summary>
    public TimeSpan FastHeadThreshold { get; init; } = TimeSpan.FromMilliseconds(40);

    public long HeadBytes { get; init; } = StorageWarmPlanner.DefaultHeadBytes;

    public long TailBytes { get; init; } = StorageWarmPlanner.DefaultTailBytes;

    public long ProbeBytes { get; init; } = StorageWarmPlanner.DefaultProbeBytes;

    /// <summary>Size of each read call.</summary>
    public int ChunkBytes { get; init; } = 1 << 20;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public IWarmFileSource Files { get; init; } = LocalWarmFileSource.Instance;

    /// <summary>Protocol of an item's path; null means <see cref="BaseItem.PathProtocol"/> (server-wired).</summary>
    public Func<BaseItem, MediaProtocol?>? ProtocolOf { get; init; }
}

/// <summary>One read: <see cref="Length"/> bytes starting at <see cref="Offset"/>.</summary>
internal readonly record struct WarmRead(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>
/// <see cref="Reads"/> is [head] for a small file (the whole file, read once) or [head, tail];
/// <see cref="WakeProbe"/> is read only when the head turns out to be a page-cache hit.
/// </summary>
internal sealed record WarmPlan(IReadOnlyList<WarmRead> Reads, WarmRead? WakeProbe)
{
    public static readonly WarmPlan Empty = new(Array.Empty<WarmRead>(), null);
}

/// <summary>Turns a file length into the reads a storage warm performs. Pure.</summary>
internal static class StorageWarmPlanner
{
    /// <summary>Covers ffmpeg's probe and the player's first range reads.</summary>
    public const long DefaultHeadBytes = 32L << 20;

    /// <summary>MP4 moov-at-end and MKV Cues (read by the first remux main.m3u8).</summary>
    public const long DefaultTailBytes = 8L << 20;

    public const long DefaultProbeBytes = 1L << 20;

    private const long ProbeAlignment = 4096;

    public static WarmPlan Plan(
        long fileLength,
        long headBytes = DefaultHeadBytes,
        long tailBytes = DefaultTailBytes,
        long probeBytes = DefaultProbeBytes)
    {
        if (fileLength <= 0 || headBytes <= 0)
        {
            return WarmPlan.Empty;
        }

        tailBytes = Math.Max(0, tailBytes);
        if (fileLength <= headBytes + tailBytes)
        {
            // Small file: one read of the whole thing, nothing left for a probe to wake.
            return new WarmPlan(new[] { new WarmRead(0, fileLength) }, null);
        }

        var head = new WarmRead(0, headBytes);
        var reads = new List<WarmRead> { head };
        var gapEnd = fileLength;
        if (tailBytes > 0)
        {
            var tail = new WarmRead(fileLength - tailBytes, tailBytes);
            reads.Add(tail);
            gapEnd = tail.Offset;
        }

        // Probe the middle of the file (least likely to be cached), kept inside the unread gap.
        var gapStart = head.End;
        var probeLength = Math.Min(Math.Max(0, probeBytes), gapEnd - gapStart);
        WarmRead? probe = null;
        if (probeLength > 0)
        {
            var offset = ((fileLength / 2) - (probeLength / 2)) / ProbeAlignment * ProbeAlignment;
            offset = Math.Clamp(offset, gapStart, gapEnd - probeLength);
            probe = new WarmRead(offset, probeLength);
        }

        return new WarmPlan(reads, probe);
    }

    public static bool ShouldProbe(TimeSpan headElapsed, TimeSpan fastThreshold) => headElapsed < fastThreshold;
}

internal enum WarmOutcome
{
    Warmed,
    Skipped,
    Deduplicated,
    Missing,
    TimedOut,
    Failed,
}

internal sealed record WarmResult(
    WarmOutcome Outcome,
    long HeadBytes = 0,
    TimeSpan Head = default,
    TimeSpan? Tail = null,
    TimeSpan? Probe = null,
    WarmPlan? Plan = null)
{
    public static WarmResult Of(WarmOutcome outcome) => new(outcome);
}

/// <summary>File access used by the warmer (swapped out in tests).</summary>
internal interface IWarmFileSource
{
    bool Exists(string path);

    IWarmFile Open(string path);
}

internal interface IWarmFile : IDisposable
{
    long Length { get; }

    int Read(Span<byte> buffer, long offset);
}

internal sealed class LocalWarmFileSource : IWarmFileSource
{
    public static readonly LocalWarmFileSource Instance = new();

    public bool Exists(string path) => File.Exists(path);

    public IWarmFile Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return new LocalWarmFile(handle, RandomAccess.GetLength(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private sealed class LocalWarmFile : IWarmFile
    {
        private readonly SafeFileHandle _handle;

        public LocalWarmFile(SafeFileHandle handle, long length)
        {
            _handle = handle;
            Length = length;
        }

        public long Length { get; }

        public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(_handle, buffer, offset);

        public void Dispose() => _handle.Dispose();
    }
}
