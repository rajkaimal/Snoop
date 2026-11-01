using System.Globalization;
using Snoop.Core.Formatting;
using Snoop.Core.Sessions;
using Snoop.Inspection.Bodies;
using Snoop.Presentation.Mvvm;

namespace Snoop.Presentation.ViewModels;

/// <summary>Drives row colour in the session grid.</summary>
public enum StatusCategory
{
    Pending,
    Success,
    Redirect,
    ClientError,
    ServerError,
    Failed,
    Tunnel,
}

/// <summary>One row of the session grid. Values are computed from the session on demand; <see cref="Refresh"/> re-reads them.</summary>
public sealed class SessionRowViewModel(Session session, SessionColumnsViewModel? columns = null) : ObservableObject
{
    private static readonly SessionColumnsViewModel DefaultColumns = new();

    public Session Session { get; } = session;

    /// <summary>The grid's column layout (shared by every row).</summary>
    public SessionColumnsViewModel Columns { get; } = columns ?? DefaultColumns;

    /// <summary>Status for sorting: codes in order, then running, then failed.</summary>
    public int ResultSortKey => Session.State is SessionState.Failed or SessionState.Aborted ? 2000 : Session.Response?.StatusCode ?? 1000;

    /// <summary>Bytes for sorting (body for HTTP, relayed bytes for tunnels).</summary>
    public long SizeSortKey => Session.Kind == SessionKind.Http ? Session.Response?.Body?.TotalBytes ?? -1 : Session.BytesSent + Session.BytesReceived;

    /// <summary>Milliseconds for sorting; running sessions last.</summary>
    public double DurationSortKey => Session.Timings.Duration?.TotalMilliseconds ?? double.MaxValue;

    public long Id => Session.Id;

    public string IdText => Session.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Small monochrome icons (Segoe Fluent Icons) for what happened to the session: paused, answered by a
    /// rule, changed, sent from the Composer, opened from a snapshot or HAR file. Empty for ordinary sessions.
    /// </summary>
    public string MarkGlyphs
    {
        get
        {
            var flags = Session.Flags;
            var marks = new System.Text.StringBuilder();
            if (Session.State == SessionState.Paused)
            {
                marks.Append(Glyphs.Pause);
            }

            if (flags.HasFlag(SessionFlags.AutoResponded))
            {
                marks.Append(Glyphs.Lightning);
            }
            else if (flags.HasFlag(SessionFlags.Modified))
            {
                marks.Append(Glyphs.Edit);
            }

            if (flags.HasFlag(SessionFlags.Composed))
            {
                marks.Append(Glyphs.Send);
            }

            if (flags.HasFlag(SessionFlags.Imported))
            {
                marks.Append(Glyphs.Folder);
            }

            return marks.ToString();
        }
    }

    /// <summary>Words for <see cref="MarkGlyphs"/>, for tooltips and screen readers.</summary>
    public string MarkText
    {
        get
        {
            var flags = Session.Flags;
            var words = new List<string>();
            if (Session.State == SessionState.Paused)
            {
                words.Add("paused at a breakpoint");
            }

            if (flags.HasFlag(SessionFlags.AutoResponded))
            {
                words.Add("answered by a rule");
            }
            else if (flags.HasFlag(SessionFlags.Modified))
            {
                words.Add("changed");
            }

            if (flags.HasFlag(SessionFlags.Composed))
            {
                words.Add("sent from the Composer");
            }

            if (flags.HasFlag(SessionFlags.Imported))
            {
                words.Add("from a file");
            }

            return string.Join(", ", words);
        }
    }

    /// <summary>Status code, or ERR / ABRT / "..." while waiting.</summary>
    public string Result => Session.State switch
    {
        SessionState.Failed => "ERR",
        SessionState.Aborted => "ABRT",
        SessionState.Paused => "BRK",
        _ when Session.Kind == SessionKind.Connect && Session.State == SessionState.Tunneling => "···",
        _ => Session.Response?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "···",
    };

    public string Method => Session.Request.Method;

    public string Protocol => Session.Kind switch
    {
        SessionKind.Connect => Session.Flags.HasFlag(SessionFlags.Decrypted) ? "TLS" : "Tunnel",
        SessionKind.Upgrade => Session.Response?.StatusCode == 101 ? "WS" : Session.Request.HttpVersion,
        _ => Session.Response?.HttpVersion ?? Session.Request.HttpVersion,
    };

    public string Host => Session.Request.Url.IsDefaultPort ? Session.Request.Url.Host : Session.Request.Url.Authority;

    /// <summary>Path and query, or the host:port for tunnels.</summary>
    public string Path => Session.Kind == SessionKind.Connect
        ? Session.Request.Url.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped)
        : Session.Request.Url.PathAndQuery;

    public string Url => Session.Request.Url.ToString();

    public string ContentType => ContentSniffer.MediaType(Session.Response?.Headers.Get("Content-Type"));

    /// <summary>A short label for the content type: JSON, HTML, JS, CSS, IMG, FONT, TXT, BIN, or TLS for tunnels.</summary>
    public string TypeBadge => Session.Kind switch
    {
        SessionKind.Connect => string.Empty,
        SessionKind.Upgrade => "WS",
        _ => ContentSniffer.FromContentType(Session.Response?.Headers.Get("Content-Type")) switch
        {
            _ when Session.Response is null || Session.Response.Body is null => string.Empty,
            ContentKind.Json => "JSON",
            ContentKind.Html => "HTML",
            ContentKind.Xml => "XML",
            ContentKind.JavaScript => "JS",
            ContentKind.Css => "CSS",
            ContentKind.Image => "IMG",
            ContentKind.Font => "FONT",
            ContentKind.Text => "TXT",
            ContentKind.Form => "FORM",
            _ => "BIN",
        },
    };

    public string Size => Session.Kind switch
    {
        SessionKind.Http => Session.Response?.Body is { } body ? ByteSize.Format(body.TotalBytes) : string.Empty,
        _ => Session.BytesSent + Session.BytesReceived > 0 ? ByteSize.Format(Session.BytesSent + Session.BytesReceived) : string.Empty,
    };

    public string Duration => Session.Timings.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : string.Empty;

    public string Process => Session.ClientProcess?.ToString() ?? string.Empty;

    public string StartedAt => Session.Timings.StartedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public StatusCategory Category => Session.State switch
    {
        SessionState.Failed or SessionState.Aborted => StatusCategory.Failed,
        _ when Session.Kind == SessionKind.Connect => StatusCategory.Tunnel,
        _ => Session.Response?.StatusCode switch
        {
            null => StatusCategory.Pending,
            >= 500 => StatusCategory.ServerError,
            >= 400 => StatusCategory.ClientError,
            >= 300 => StatusCategory.Redirect,
            _ => StatusCategory.Success,
        },
    };

    /// <summary>Error or note, shown as a tooltip.</summary>
    public string Detail => MarkText.Length > 0 && Session.State != SessionState.Paused && Session.Error is null && Session.Note is null
        ? $"{Url}\n{char.ToUpperInvariant(MarkText[0])}{MarkText[1..]}" + (Session.ModifiedBy is { } changed ? $" ({changed})" : string.Empty)
        : Session.State == SessionState.Paused
        ? "Paused at a breakpoint. Open the Breakpoints tab to edit and resume it."
        : Session.Error ?? Session.Note ?? (Session.ModifiedBy is { } by ? $"{Url}\nChanged by {by}" : Url);

    /// <summary>Tells bindings that everything may have changed (the session progressed).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>Segoe Fluent Icons code points used by the grid.</summary>
public static class Glyphs
{
    public const string Pause = "\uE769";
    public const string Lightning = "\uE945";
    public const string Edit = "\uE70F";
    public const string Send = "\uE724";
    public const string Folder = "\uE838";
}
