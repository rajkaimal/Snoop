using Snoop.Hosting.Rules;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;
using Snoop.Proxy.Rules;

namespace Snoop.Presentation.Tests.ViewModels;

public sealed class RulesViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "snoop-tests", Guid.NewGuid().ToString("N"));
    private readonly RuleSet _ruleSet = new();
    private readonly List<(string Text, bool IsError)> _messages = [];
    private readonly RulesViewModel _vm;

    public RulesViewModelTests()
    {
        Directory.CreateDirectory(_directory);
        _vm = new RulesViewModel(_ruleSet, RulesPath, new FakeOpenFiles { Path = "/tmp/answer.json" }, (text, error) => _messages.Add((text, error)));
    }

    private string RulesPath => Path.Combine(_directory, "rules.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void New_rule_is_selected_and_needs_a_pattern()
    {
        _vm.AddCommand.Execute(null);

        var rule = Assert.Single(_vm.Items);
        Assert.Same(rule, _vm.Selected);
        Assert.Equal("Rule 1", rule.Name);
        Assert.True(rule.HasError);
        Assert.Equal("Enter a URL pattern.", rule.Error);
        Assert.Equal("Rules", _vm.HeaderText);
        Assert.Empty(_ruleSet.Match("GET", new Uri("https://example.com/")));
    }

    [Fact]
    public void Edits_apply_at_once_and_are_saved()
    {
        _vm.AddCommand.Execute(null);
        var rule = _vm.Selected!;

        rule.Pattern = "example.com/api";
        rule.Actions[0].Value = "{\"mock\":1}";

        Assert.False(rule.HasError);
        Assert.Equal("Rules (1 on)", _vm.HeaderText);
        var live = Assert.Single(_ruleSet.Match("GET", new Uri("https://example.com/api/items")));
        Assert.Equal("{\"mock\":1}", live.Actions[0].Value);
        var saved = Assert.Single(RulesFile.Load(RulesPath));
        Assert.Equal("example.com/api", saved.Pattern);
        Assert.Equal("example.com/api → Respond 200", rule.Summary);
    }

    [Fact]
    public void Load_reads_the_file_and_applies_it()
    {
        RulesFile.Save([new TrafficRule { Name = "saved", Pattern = "x", Actions = [new RuleAction(RuleActionKind.Block)] }], RulesPath);

        _vm.Load();

        Assert.Equal("saved", Assert.Single(_vm.Items).Name);
        Assert.Equal(0, _vm.SelectedIndex);
        Assert.Single(_ruleSet.Match("GET", new Uri("https://x.example/")));
    }

    [Fact]
    public void Damaged_file_is_reported()
    {
        File.WriteAllText(RulesPath, "{ nope");

        _vm.Load();

        Assert.Contains(_messages, m => m.IsError && m.Text.StartsWith("Could not load rules", StringComparison.Ordinal));
        Assert.Empty(_vm.Items);
    }

    [Fact]
    public void Rules_can_be_reordered_duplicated_and_removed()
    {
        _vm.Add(new TrafficRule { Name = "a", Pattern = "x", Actions = [new RuleAction(RuleActionKind.Block)] });
        _vm.Add(new TrafficRule { Name = "b", Pattern = "x", Actions = [new RuleAction(RuleActionKind.Block)] });
        Assert.Equal(1, _vm.SelectedIndex);
        Assert.False(_vm.MoveDownCommand.CanExecute(null));

        _vm.MoveUpCommand.Execute(null);
        Assert.Equal(new[] { "b", "a" }, _vm.Items.Select(i => i.Name));
        Assert.Equal(new[] { "b", "a" }, _ruleSet.Rules.Select(r => r.Name));

        _vm.DuplicateCommand.Execute(null);
        Assert.Equal(new[] { "b", "b (copy)", "a" }, _vm.Items.Select(i => i.Name));
        Assert.NotEqual(_vm.Items[0].ToRule().Id, _vm.Items[1].ToRule().Id);

        _vm.RemoveCommand.Execute(null);
        Assert.Equal(new[] { "b", "a" }, _vm.Items.Select(i => i.Name));
        Assert.Equal(1, _vm.SelectedIndex);
        Assert.Equal(2, RulesFile.Load(RulesPath).Count);
    }

    [Fact]
    public void Action_kind_changes_labels_and_defaults()
    {
        var action = new RuleActionViewModel(new RuleAction(RuleActionKind.Delay, "", "100"), new FakeOpenFiles());
        Assert.False(action.ShowName);
        Assert.Equal("Milliseconds", action.ValueLabel);

        action.KindIndex = (int)RuleActionKind.RespondWithFile;

        Assert.Equal("200", action.Name);
        Assert.Equal("Status", action.NameLabel);
        Assert.Equal("File", action.ValueLabel);
        Assert.True(action.CanBrowse);
        Assert.True(action.IsSingleLineValue);

        action.KindIndex = (int)RuleActionKind.RemoveResponseHeader;
        Assert.False(action.ShowValue);
        Assert.Equal(new RuleAction(RuleActionKind.RemoveResponseHeader, "200", ""), action.ToAction());
    }

    [Fact]
    public async Task Browse_fills_the_file_path()
    {
        var action = new RuleActionViewModel(new RuleAction(RuleActionKind.RespondWithFile, "200", ""), new FakeOpenFiles { Path = "/tmp/answer.json" });

        await action.BrowseCommand.ExecuteAsync();

        Assert.Equal("/tmp/answer.json", action.Value);
    }

    [Fact]
    public void Removing_and_adding_actions_updates_the_rule()
    {
        var editor = _vm.Add(new TrafficRule { Pattern = "x", Actions = [new RuleAction(RuleActionKind.Block)] });

        editor.Actions[0].RemoveCommand.Execute(null);
        Assert.True(editor.HasError);

        editor.AddActionCommand.Execute(null);
        editor.Actions[0].Name = "X-Test";
        editor.Actions[0].Value = "1";

        Assert.False(editor.HasError);
        Assert.Equal(RuleActionKind.SetResponseHeader, Assert.Single(_ruleSet.Rules[0].Actions).Kind);
    }

    [Fact]
    public void Rule_from_a_session_answers_its_url_with_the_captured_response()
    {
        var session = TestHost.NewSession("https://api.example.com/v1/user?id=3");
        TestHost.Respond(session, 200, "application/json", "{\"name\":\"ada\"}");

        var editor = _vm.AddFromSession(session);

        var rule = editor.ToRule();
        Assert.Equal(RuleMatchKind.Exact, rule.MatchKind);
        Assert.Equal("https://api.example.com/v1/user?id=3", rule.Pattern);
        Assert.Equal("GET", rule.Method);
        Assert.Equal(new RuleAction(RuleActionKind.Respond, "200", "{\"name\":\"ada\"}"), rule.Actions[0]);
        Assert.Equal(new RuleAction(RuleActionKind.SetResponseHeader, "Content-Type", "application/json"), rule.Actions[1]);
        Assert.False(editor.HasError);
        Assert.Single(_ruleSet.Match("GET", new Uri("https://api.example.com/v1/user?id=3")));
    }

    [Fact]
    public void Hidden_single_line_box_cannot_cut_a_multi_line_body()
    {
        var action = new RuleActionViewModel(new RuleAction(RuleActionKind.Respond, "200", "line1\nline2"));

        action.SingleLineValue = "line1";
        action.MultilineValue = "line1\rline2";

        Assert.Equal("line1\nline2", action.Value);
    }
}
