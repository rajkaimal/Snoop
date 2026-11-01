using System.Globalization;
using Snoop.Core.Sessions;
using Snoop.Presentation.Mvvm;

namespace Snoop.Presentation.ViewModels;

/// <summary>
/// One bar of the waterfall. The four parts add up to 1 (the whole time range): space before the
/// request, sending (including connecting), waiting for the first byte, and receiving.
/// </summary>
public sealed record TimelineRow(
    long Id,
    string Label,
    string Detail,
    double Offset,
    double Send,
    double Wait,
    double Receive,
    double After,
    StatusCategory Category);

/// <summary>The Timeline tab: a waterfall of the shown HTTP sessions, oldest at the top.</summary>
public sealed class TimelineViewModel : ObservableObject
{
    /// <summary>Most bars drawn; the latest sessions are kept.</summary>
    public const int MaxRows = 500;

    /// <summary>Smallest part of the width a non-empty phase gets, so short requests stay visible.</summary>
    private const double MinPart = 0.002;

    private IReadOnlyList<TimelineRow> _rows = new List<TimelineRow>();
    private string _rangeText = "Nothing to show yet.";

    public IReadOnlyList<TimelineRow> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public string RangeText
    {
        get => _rangeText;
        private set => SetProperty(ref _rangeText, value);
    }

    /// <summary>Rebuilds the waterfall from <paramref name="sessions"/> (tunnels are left out).</summary>
    public void Build(IReadOnlyList<Session> sessions, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var clock = now ?? DateTimeOffset.Now;
        var shown = sessions.Where(s => s.Kind != SessionKind.Connect).OrderBy(s => s.Timings.StartedAt).ThenBy(s => s.Id).ToList();
        var skipped = Math.Max(0, shown.Count - MaxRows);
        if (skipped > 0)
        {
            shown = shown.Skip(skipped).ToList();
        }

        if (shown.Count == 0)
        {
            Rows = new List<TimelineRow>();
            RangeText = "No HTTP sessions are shown. (Tunnels are left out of the timeline.)";
            return;
        }

        var start = shown[0].Timings.StartedAt;
        var end = shown.Max(s => s.Timings.StartedAt + (s.Timings.Duration ?? (clock - s.Timings.StartedAt)));
        var range = Math.Max(1, (end - start).TotalMilliseconds);

        Rows = shown.Select(s => Bar(s, start, range, clock)).ToList();
        RangeText = $"{shown.Count.ToString("N0", CultureInfo.InvariantCulture)} request(s) over {FormatMs(range)}"
                    + (skipped > 0 ? $" (the latest {MaxRows}; {skipped.ToString("N0", CultureInfo.InvariantCulture)} earlier not shown)" : string.Empty)
                    + ". Bars: send · wait for first byte · receive.";
    }

    private static TimelineRow Bar(Session session, DateTimeOffset start, double range, DateTimeOffset clock)
    {
        var t = session.Timings;
        var offset = (t.StartedAt - start).TotalMilliseconds;
        var total = t.Duration?.TotalMilliseconds ?? (clock - t.StartedAt).TotalMilliseconds;
        var sent = Math.Min(t.RequestSent?.TotalMilliseconds ?? total, total);
        var firstByte = Math.Clamp(t.ResponseHeadersReceived?.TotalMilliseconds ?? total, sent, total);

        var send = Part(sent, range);
        var wait = Part(firstByte - sent, range);
        var receive = Part(total - firstByte, range);
        var offsetPart = Math.Clamp(offset / range, 0, 1);
        if (send + wait + receive < MinPart)
        {
            send = MinPart;
        }

        // Keep the bar inside the range when minimum widths pushed it over.
        var overflow = offsetPart + send + wait + receive - 1;
        if (overflow > 0)
        {
            offsetPart = Math.Max(0, offsetPart - overflow);
        }

        var after = Math.Max(0, 1 - offsetPart - send - wait - receive);
        var row = new SessionRowViewModel(session);
        var status = session.Response is { } r ? r.StatusCode.ToString(CultureInfo.InvariantCulture) : row.Result;
        var detail = $"#{session.Id.ToString(CultureInfo.InvariantCulture)} {session.Request.Method} {session.Request.Url} → {status}\n"
                     + $"starts at +{FormatMs(offset)} · send {FormatMs(sent)} · wait {FormatMs(firstByte - sent)} · receive {FormatMs(total - firstByte)} · total {FormatMs(total)}"
                     + (session.IsFinished ? string.Empty : " (still running)");
        var label = $"{session.Request.Method} {row.Host}{session.Request.Url.AbsolutePath}";
        return new TimelineRow(session.Id, label, detail, offsetPart, send, wait, receive, after, row.Category);
    }

    private static double Part(double ms, double range) => ms <= 0 ? 0 : Math.Max(MinPart, ms / range);

    private static string FormatMs(double ms) =>
        ms >= 1000 ? $"{(ms / 1000).ToString("0.00", CultureInfo.InvariantCulture)} s" : $"{ms.ToString("0", CultureInfo.InvariantCulture)} ms";
}
