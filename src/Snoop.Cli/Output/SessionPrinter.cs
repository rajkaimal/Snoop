using System.Globalization;
using Snoop.Core.Formatting;
using Snoop.Core.Sessions;

namespace Snoop.Cli.Output;

/// <summary>Prints one line per finished session, e.g. <c>#12  200 GET  https://example.com/  1.2 KB  35 ms  chrome:1234</c>.</summary>
internal sealed class SessionPrinter(ConsoleWriter writer, bool showTunnels)
{
    public void Attach(SessionStore store) => store.SessionChanged += OnSessionChanged;

    public void Detach(SessionStore store) => store.SessionChanged -= OnSessionChanged;

    internal static string Format(Session session)
    {
        var id = $"#{session.Id}".PadRight(6);
        var result = session.State switch
        {
            SessionState.Failed => "ERR",
            SessionState.Aborted => "ABRT",
            _ => session.Response?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "---",
        };

        var method = session.Request.Method.PadRight(7);
        var url = session.Kind == SessionKind.Connect
            ? session.Request.Url.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped)
            : session.Request.Url.ToString();
        var size = session.Kind switch
        {
            SessionKind.Http => ByteSize.Format(session.Response?.Body?.TotalBytes ?? 0),
            _ => $"{ByteSize.Format(session.BytesSent)} up / {ByteSize.Format(session.BytesReceived)} down",
        };

        var duration = session.Timings.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "-";
        var process = session.ClientProcess?.ToString() ?? string.Empty;
        var line = $"{id} {result,-4} {method} {url}  {size}  {duration}  {process}".TrimEnd();
        if ((session.Error ?? session.Note) is { } detail)
        {
            line += $"{Environment.NewLine}       {detail}";
        }

        return line;
    }

    private static ConsoleColor ColorFor(Session session) =>
        session.State switch
        {
            SessionState.Failed or SessionState.Aborted => ConsoleColor.Red,
            _ when session.Kind != SessionKind.Http => ConsoleColor.DarkGray,
            _ => session.Response?.StatusCode switch
            {
                >= 500 => ConsoleColor.Red,
                >= 400 => ConsoleColor.Yellow,
                >= 300 => ConsoleColor.Cyan,
                _ => ConsoleColor.Gray,
            },
        };

    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        if (e.Change != SessionChange.Finished)
        {
            return;
        }

        var session = e.Session;

        // Decrypted tunnels just carry the requests printed separately; show them only on request or on failure.
        if (session.Kind == SessionKind.Connect && !showTunnels && session.State == SessionState.Completed
            && session.Flags.HasFlag(SessionFlags.Decrypted) && session.Note is null)
        {
            return;
        }

        writer.WriteLine(Format(session), ColorFor(session));
    }
}
