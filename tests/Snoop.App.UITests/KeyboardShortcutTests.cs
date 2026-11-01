using FlaUI.Core.AutomationElements;
using static Snoop.App.UITests.Keys;

namespace Snoop.App.UITests;

/// <summary>
/// Presses every keyboard shortcut in the real app and checks the result. The demo snapshot
/// (docs/demo.snoop) is open: row 0 is GET /posts/1, row 2 is /comments?postId=1, 15 rows in all.
/// Ctrl+S and Ctrl+O in the main window open Windows' file dialogs and are not tested here.
/// </summary>
public sealed class KeyboardShortcutTests : IDisposable
{
    private const string PostsUrl = "https://jsonplaceholder.typicode.com/posts/1";
    private const string CommentsUrl = "https://jsonplaceholder.typicode.com/comments?postId=1";

    private readonly SnoopApp _snoop = new();

    public void Dispose() => _snoop.Dispose();

    [Fact]
    public void Ctrl_F_goes_to_the_search_box()
    {
        _snoop.ClickRow(0);
        SnoopApp.Press(Control, Key('F'));
        SnoopApp.WaitUntil(() => _snoop.FocusedId == "SearchBox", "the search box to have focus");
    }

    [Fact]
    public void Enter_F3_and_Shift_F3_step_through_matches()
    {
        SnoopApp.Press(Control, Key('F'));
        SnoopApp.Type("jsonplaceholder");
        SnoopApp.WaitUntil(() => _snoop.CountText.StartsWith("6 of 15", StringComparison.Ordinal), "the search to show 6 of 15");

        SnoopApp.Press(Enter);
        SnoopApp.WaitUntil(() => _snoop.SelectedRows.SequenceEqual([0]), "Enter to select the first match");
        SnoopApp.Press(F3);
        SnoopApp.WaitUntil(() => _snoop.SelectedRows.SequenceEqual([1]), "F3 to select the next match");
        SnoopApp.Press(Shift, Enter);
        SnoopApp.WaitUntil(() => _snoop.SelectedRows.SequenceEqual([0]), "Shift+Enter to go back");
        SnoopApp.Press(Shift, F3);
        SnoopApp.WaitUntil(() => _snoop.SelectedRows.SequenceEqual([5]), "Shift+F3 to wrap to the last match");
    }

    [Fact]
    public void Esc_clears_the_search_then_returns_to_the_list()
    {
        SnoopApp.Press(Control, Key('F'));
        SnoopApp.Type("httpbin");
        SnoopApp.WaitUntil(() => _snoop.CountText.StartsWith("3 of 15", StringComparison.Ordinal), "the search to show 3 of 15");

        SnoopApp.Press(Escape);
        SnoopApp.WaitUntil(() => _snoop.SearchBox.Text.Length == 0 && _snoop.CountText == "15 sessions", "Esc to clear the search");
        Assert.Equal("SearchBox", _snoop.FocusedId);

        SnoopApp.Press(Escape);
        SnoopApp.WaitUntil(() => _snoop.FocusedId != "SearchBox", "a second Esc to leave the search box");
    }

    [Fact]
    public void Ctrl_L_clears_the_list_but_not_while_typing()
    {
        SnoopApp.Press(Control, Key('F'));
        SnoopApp.Press(Control, Key('L'));
        Thread.Sleep(500);
        Assert.Equal("15 sessions", _snoop.CountText);

        _snoop.ClickRow(0);
        SnoopApp.Press(Control, Key('L'));
        SnoopApp.WaitUntil(() => _snoop.CountText == "0 sessions", "Ctrl+L to clear the list");
    }

    [Fact]
    public void Ctrl_C_and_Ctrl_Shift_C_copy_every_selected_session()
    {
        SnoopApp.ClearClipboard();
        _snoop.ClickRow(0);
        _snoop.ClickRow(2, control: true);
        SnoopApp.WaitUntil(() => _snoop.SelectedRows.SequenceEqual([0, 2]), "two rows to be selected");

        SnoopApp.Press(Control, Key('C'));
        SnoopApp.WaitUntil(() => SnoopApp.ClipboardText() == $"{PostsUrl}\n{CommentsUrl}", "Ctrl+C to copy both URLs");

        SnoopApp.Press(Control, Shift, Key('C'));
        SnoopApp.WaitUntil(
            () => SnoopApp.ClipboardText() is var text && text.StartsWith("curl ", StringComparison.Ordinal) && text.Split("curl ").Length == 3,
            "Ctrl+Shift+C to copy two cURL commands");
    }

    [Fact]
    public void Delete_removes_the_selected_sessions()
    {
        _snoop.ClickRow(0);
        _snoop.ClickRow(1, control: true);
        SnoopApp.Press(Delete);
        SnoopApp.WaitUntil(() => _snoop.CountText == "13 sessions", "Delete to remove two sessions");
    }

    [Fact]
    public void Ctrl_E_opens_the_request_in_the_Composer()
    {
        _snoop.ClickRow(0);
        SnoopApp.Press(Control, Key('E'));
        SnoopApp.WaitUntil(() => _snoop.ToolTitle == "Composer", "Ctrl+E to open the Composer");
        SnoopApp.WaitUntil(() => _snoop.ById("ComposerUrl")?.AsTextBox().Text == PostsUrl, "the Composer to hold the request's URL");
    }

    [Fact]
    public void Ctrl_R_sends_again_and_explains_that_capture_must_run()
    {
        _snoop.ClickRow(0);
        SnoopApp.Press(Control, Key('R'));
        SnoopApp.WaitUntil(() => _snoop.MessageText.Contains("Start capturing first", StringComparison.Ordinal), "Ctrl+R to report that capture is off");
    }

    [Fact]
    public void Ctrl_1_to_Ctrl_6_switch_tools()
    {
        string?[] expected = [null, "Composer", "Rules", "Breakpoints", "Timeline", "Compare"];
        foreach (var index in new[] { 1, 2, 3, 4, 5, 0 })
        {
            SnoopApp.Press(Control, Key((char)('1' + index)));
            SnoopApp.WaitUntil(() => _snoop.ToolTitle == expected[index], $"Ctrl+{index + 1} to show {expected[index] ?? "Inspect"}");
        }

        Assert.NotNull(_snoop.ById("InspectorTitle"));
    }

    [Fact]
    public void Alt_arrows_go_back_and_forward()
    {
        _snoop.ClickRow(0);
        _snoop.ClickRow(2);
        _snoop.ClickRow(4);

        SnoopApp.Press(Alt, Left);
        SnoopApp.WaitUntil(() => _snoop.SelectedRows.SequenceEqual([2]), "Alt+Left to go back to row 2");
        SnoopApp.Press(Alt, Left);
        SnoopApp.WaitUntil(() => _snoop.SelectedRows.SequenceEqual([0]), "Alt+Left to go back to row 0");
        SnoopApp.Press(Alt, Right);
        SnoopApp.WaitUntil(() => _snoop.SelectedRows.SequenceEqual([2]), "Alt+Right to go forward to row 2");
    }

    [Fact]
    public void Composer_Ctrl_Enter_sends_and_Ctrl_S_saves()
    {
        SnoopApp.Press(Control, Key('2'));
        SnoopApp.WaitUntil(() => _snoop.ToolTitle == "Composer", "the Composer");
        var url = _snoop.ById("ComposerUrl")!.AsTextBox();
        url.Focus();
        url.Text = "https://example.com/";

        SnoopApp.Press(Control, Enter);
        SnoopApp.WaitUntil(
            () => _snoop.ById("ComposerResult")?.Name.Contains("Start capturing first", StringComparison.Ordinal) == true,
            "Ctrl+Enter to try sending");

        SnoopApp.Press(Control, Key('S'));
        SnoopApp.WaitUntil(() => _snoop.Window.FindFirstDescendant(cf => cf.ByName("Save request")) is not null, "Ctrl+S to ask for a name");
        SnoopApp.Press(Escape);
    }
}
