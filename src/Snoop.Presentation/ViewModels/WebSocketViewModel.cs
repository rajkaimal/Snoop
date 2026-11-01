using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Snoop.Core.Formatting;
using Snoop.Core.Sessions;
using Snoop.Inspection.Formatting;
using Snoop.Presentation.Mvvm;

namespace Snoop.Presentation.ViewModels;

/// <summary>One line in the WebSocket message list.</summary>
public sealed class WebSocketMessageRow(WebSocketMessage message)
{
    private const int PreviewChars = 160;

    public WebSocketMessage Message { get; } = message;

    /// <summary>↑ app to server, ↓ server to app.</summary>
    public string Direction => Message.FromClient ? "↑" : "↓";

    public string DirectionText => Message.FromClient ? "Sent" : "Received";

    public bool FromClient => Message.FromClient;

    public string Time => Message.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string Kind => Message.Opcode.ToString();

    public string Size => ByteSize.Format(Message.Length) + (Message.WasCompressed ? " (deflate)" : string.Empty);

    /// <summary>The start of the message on one line.</summary>
    public string Preview
    {
        get
        {
            var text = Message.Opcode switch
            {
                WebSocketOpcode.Text when Message.Note is null => Encoding.UTF8.GetString(Message.Payload, 0, Math.Min(Message.Payload.Length, PreviewChars * 4)),
                WebSocketOpcode.Close => WebSocketViewModel.DescribeClose(Message.Payload),
                WebSocketOpcode.Ping or WebSocketOpcode.Pong => Message.Payload.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Message.Payload),
                _ => $"{ByteSize.Format(Message.Payload.Length)} binary",
            };
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            return text.Length > PreviewChars ? text[..PreviewChars] + "…" : text;
        }
    }
}

/// <summary>The WebSocket tab: the messages of the selected upgraded connection.</summary>
public sealed class WebSocketViewModel : ObservableObject
{
    private Session? _session;
    private int _selectedIndex = -1;
    private string _detailText = string.Empty;
    private string _summary = "Select a WebSocket session to see its messages.";

    public ObservableCollection<WebSocketMessageRow> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var index = value >= -1 && value < Messages.Count ? value : -1;
            if (SetProperty(ref _selectedIndex, index))
            {
                DetailText = index < 0 ? string.Empty : FormatDetail(Messages[index].Message);
            }
        }
    }

    /// <summary>The selected message in full: pretty JSON, text, or hex.</summary>
    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    /// <summary>Shows <paramref name="session"/>'s messages; null or a non-WebSocket session clears the list.</summary>
    public void Show(Session? session)
    {
        if (!ReferenceEquals(session, _session))
        {
            _session = session;
            Messages.Clear();
            SelectedIndex = -1;
            DetailText = string.Empty;
        }

        Refresh();
    }

    /// <summary>Adds messages that arrived since the last call.</summary>
    public void Refresh()
    {
        if (_session?.WebSocket is not { } log)
        {
            Summary = _session is null
                ? "Select a WebSocket session to see its messages."
                : "This session is not a WebSocket connection.";
            OnPropertyChanged(nameof(HasMessages));
            return;
        }

        foreach (var message in log.Snapshot(Messages.Count))
        {
            Messages.Add(new WebSocketMessageRow(message));
        }

        var sent = Messages.Count(m => m.FromClient);
        var dropped = log.DroppedCount;
        Summary = $"{Messages.Count.ToString("N0", CultureInfo.InvariantCulture)} message(s): {sent.ToString("N0", CultureInfo.InvariantCulture)} sent, "
                  + $"{(Messages.Count - sent).ToString("N0", CultureInfo.InvariantCulture)} received"
                  + (dropped > 0 ? $" ({dropped.ToString("N0", CultureInfo.InvariantCulture)} more not kept)" : string.Empty)
                  + (_session.IsFinished ? " · closed" : " · open");
        OnPropertyChanged(nameof(HasMessages));
    }

    internal static string FormatDetail(WebSocketMessage message)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{(message.FromClient ? "Sent by the app" : "Received from the server")} · {message.Opcode} · {ByteSize.Format(message.Length)}");
        if (message.WasCompressed)
        {
            builder.Append(" compressed");
        }

        if (message.IsTruncated)
        {
            builder.Append(CultureInfo.InvariantCulture, $" · first {ByteSize.Format(message.Payload.Length)} shown");
        }

        builder.Append("\r\n");
        if (message.Note is { } note)
        {
            builder.Append(note).Append("\r\n");
        }

        builder.Append("\r\n");
        switch (message.Opcode)
        {
            case WebSocketOpcode.Text when message.Note is null:
                var text = Encoding.UTF8.GetString(message.Payload);
                builder.Append(JsonFormatter.TryFormat(text) ?? text);
                break;
            case WebSocketOpcode.Close:
                builder.Append(DescribeClose(message.Payload));
                break;
            default:
                builder.Append(message.Payload.Length == 0 ? "(empty)" : HexDump.Format(message.Payload));
                break;
        }

        return builder.ToString();
    }

    internal static string DescribeClose(byte[] payload)
    {
        if (payload.Length < 2)
        {
            return "Close";
        }

        var code = (payload[0] << 8) | payload[1];
        var reason = payload.Length > 2 ? Encoding.UTF8.GetString(payload, 2, payload.Length - 2) : string.Empty;
        return reason.Length == 0 ? $"Close {code.ToString(CultureInfo.InvariantCulture)}" : $"Close {code.ToString(CultureInfo.InvariantCulture)}: {reason}";
    }
}
