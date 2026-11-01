using System.Diagnostics;

namespace Snoop.Core.Sessions;

/// <summary>
/// Monotonic timestamps for a session, measured from when the request head arrived.
/// Each mark is set once by the proxy.
/// </summary>
public sealed class SessionTimings
{
    private readonly long _startTimestamp;
    private long _requestSent;
    private long _responseHeaders;
    private long _completed;

    internal SessionTimings(TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        StartedAt = clock.GetUtcNow();
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Rebuilds timings saved earlier (snapshots). Marks that were not set stay unset.</summary>
    internal static SessionTimings Restore(DateTimeOffset startedAt, TimeSpan? requestSent, TimeSpan? responseHeaders, TimeSpan? completed)
    {
        var timings = new SessionTimings(startedAt);
        timings._requestSent = ToStored(requestSent);
        timings._responseHeaders = ToStored(responseHeaders);
        timings._completed = ToStored(completed);
        return timings;
    }

    private SessionTimings(DateTimeOffset startedAt)
    {
        StartedAt = startedAt;
        _startTimestamp = 0;
    }

    /// <summary>Wall-clock time the request head arrived.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When the full request had been handed to the server.</summary>
    public TimeSpan? RequestSent => ToSpan(Interlocked.Read(ref _requestSent));

    /// <summary>When the response status line and headers arrived (time to first byte).</summary>
    public TimeSpan? ResponseHeadersReceived => ToSpan(Interlocked.Read(ref _responseHeaders));

    /// <summary>When the exchange ended, successfully or not.</summary>
    public TimeSpan? Completed => ToSpan(Interlocked.Read(ref _completed));

    /// <summary>Total duration, or null while the session is still running.</summary>
    public TimeSpan? Duration => Completed;

    internal void MarkRequestSent() => Mark(ref _requestSent);

    internal void MarkResponseHeaders() => Mark(ref _responseHeaders);

    internal void MarkCompleted() => Mark(ref _completed);

    private void Mark(ref long field)
    {
        var elapsed = Stopwatch.GetTimestamp() - _startTimestamp;

        // Stored as elapsed + 1 so that zero means "not set".
        Interlocked.CompareExchange(ref field, elapsed + 1, 0);
    }

    private static long ToStored(TimeSpan? span) =>
        span is { } value && value >= TimeSpan.Zero
            ? (long)(value.Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond) + 1
            : 0;

    private static TimeSpan? ToSpan(long stored) =>
        stored == 0 ? null : Stopwatch.GetElapsedTime(0, stored - 1);
}
