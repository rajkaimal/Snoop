using System.Text;
using System.Text.Json;
using Snoop.Core.Sessions;
using Snoop.Inspection.Export;
using static Snoop.Inspection.Tests.TestSessions;

namespace Snoop.Inspection.Tests.Export;

public class WebSocketHarTests
{
    [Fact]
    public void WebSocket_messages_use_chromes_har_extension()
    {
        var session = Create(url: "https://chat.example/ws", status: 101, kind: SessionKind.Upgrade);
        session.WebSocket = new WebSocketLog();
        session.WebSocket.Add(new WebSocketMessage(1, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_500), true, WebSocketOpcode.Text, 2, Encoding.UTF8.GetBytes("hi"), false, false, null));
        session.WebSocket.Add(new WebSocketMessage(2, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_001_000), false, WebSocketOpcode.Binary, 2, [1, 2], false, false, null));

        using var har = JsonDocument.Parse(HarExporter.WriteToString([session]));

        var messages = har.RootElement.GetProperty("log").GetProperty("entries")[0].GetProperty("_webSocketMessages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("send", messages[0].GetProperty("type").GetString());
        Assert.Equal("hi", messages[0].GetProperty("data").GetString());
        Assert.Equal(1_700_000_000.5, messages[0].GetProperty("time").GetDouble());
        Assert.Equal("receive", messages[1].GetProperty("type").GetString());
        Assert.Equal(2, messages[1].GetProperty("opcode").GetInt32());
        Assert.Equal("AQI=", messages[1].GetProperty("data").GetString());
    }
}
