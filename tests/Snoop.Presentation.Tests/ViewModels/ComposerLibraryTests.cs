using System.Text;
using Snoop.Presentation.Composer;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;

namespace Snoop.Presentation.Tests.ViewModels;

public sealed class ComposerVariablesTests
{
    [Fact]
    public void Fills_known_names_and_reports_missing_ones()
    {
        var missing = new HashSet<string>();
        var vars = new Dictionary<string, string> { ["base"] = "https://api.test", ["id"] = "{{not-again}}" };

        var text = ComposerVariables.Resolve("{{base}}/users/{{ id }}?t={{token}}", vars, missing);

        Assert.Equal("https://api.test/users/{{not-again}}?t={{token}}", text);
        Assert.Equal(["token"], missing);
    }

    [Fact]
    public void Built_in_names()
    {
        var missing = new HashSet<string>();
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var text = ComposerVariables.Resolve("{{$timestamp}} {{$isoTimestamp}} {{$guid}} {{$randomInt}}", new Dictionary<string, string>(), missing, now);

        var parts = text.Split(' ');
        Assert.Equal("1793448000", parts[0]);
        Assert.Equal("2026-10-31T12:00:00.000Z", parts[1]);
        Assert.True(Guid.TryParse(parts[2], out _));
        Assert.InRange(int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture), 0, 999);
        Assert.Empty(missing);
    }
}

public sealed class ComposerEnvironmentsTests
{
    private const string Text = """
        # shared
        token = abc
        [Local]
        base = http://localhost:5000
        [Staging]
        base = https://staging.test
        token = stage-token
        """;

    [Fact]
    public void Parses_globals_and_environments()
    {
        Assert.True(ComposerEnvironments.TryParse(Text, "staging", out var envs, out var error), error);

        Assert.Equal("Staging", envs!.Active);
        Assert.Equal(["Local", "Staging"], envs.Environments.Select(e => e.Name));
        var vars = envs.ActiveVariables();
        Assert.Equal("https://staging.test", vars["base"]);
        Assert.Equal("stage-token", vars["token"]);
        Assert.Equal("abc", (envs with { Active = "Local" }).ActiveVariables()["token"]);
    }

    [Theory]
    [InlineData("just words", "name = value")]
    [InlineData("[Open", "brackets")]
    [InlineData("[A]\n[a]", "two environments")]
    [InlineData("bad name = 1", "not a valid variable name")]
    public void Explains_mistakes(string text, string expected)
    {
        Assert.False(ComposerEnvironments.TryParse(text, null, out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_and_file_round_trip()
    {
        Assert.True(ComposerEnvironments.TryParse(Text, "Local", out var envs, out _));
        Assert.True(ComposerEnvironments.TryParse(envs!.ToText(), "Local", out var again, out _));
        Assert.Equal(envs.ToText(), again!.ToText());

        var path = Path.Combine(Path.GetTempPath(), $"snoop-env-{Guid.NewGuid():N}.json");
        try
        {
            envs.Save(path);
            var loaded = ComposerEnvironments.Load(path);
            Assert.Equal("Local", loaded.Active);
            Assert.Equal("http://localhost:5000", loaded.ActiveVariables()["base"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void View_model_picks_and_edits()
    {
        var vm = new EnvironmentsViewModel(null);
        Assert.Equal([EnvironmentsViewModel.NoEnvironment], vm.Choices);

        vm.BeginEdit();
        vm.EditText = "[Local]\nbase = http://localhost";
        Assert.True(vm.ApplyEdit());
        Assert.Equal("Local", vm.ActiveName); // the first environment becomes active
        Assert.Equal(1, vm.SelectedIndex);
        Assert.Equal("http://localhost", vm.Variables["base"]);

        vm.SelectedIndex = 0;
        Assert.Empty(vm.Variables);

        vm.EditText = "oops";
        Assert.False(vm.ApplyEdit());
        Assert.True(vm.HasEditError);
    }
}

public sealed class RequestLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"snoop-lib-{Guid.NewGuid():N}");
    private readonly RequestLibrary _library;

    public RequestLibraryTests() => _library = new RequestLibrary(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static SavedRequest Request(string url = "{{base}}/x") => new()
    {
        Method = "POST",
        Url = url,
        Headers = [new SavedHeader("Accept", "*/*"), new SavedHeader("X-Off", "1", Enabled: false)],
        Body = "{}",
    };

    [Fact]
    public void Saves_lists_and_loads()
    {
        var folder = _library.CreateFolder(null, "Users API");
        var path = _library.SaveNew(folder, "Get: user?", Request());
        _library.SaveNew(null, "Alpha", Request());

        Assert.Equal("Users API/Get- user-", path);
        var entries = _library.List();
        Assert.Equal(["Users API", "Alpha"], entries.Select(e => e.Name)); // folders first
        Assert.Equal("Get- user-", Assert.Single(entries[0].Children).Name);

        var loaded = _library.Load(path);
        Assert.Equal("POST", loaded.Method);
        Assert.Equal("{{base}}/x", loaded.Url);
        Assert.False(loaded.Headers[1].Enabled);
        Assert.Equal(["Users API"], _library.FolderPaths());
    }

    [Fact]
    public void Names_are_made_unique()
    {
        Assert.Equal("Same", _library.SaveNew(null, "Same", Request()));
        Assert.Equal("Same 2", _library.SaveNew(null, "Same", Request()));
        Assert.Equal("Same copy", _library.Duplicate("Same"));
        Assert.Equal("Same copy 2", _library.Duplicate("Same"));
    }

    [Fact]
    public void Rename_and_delete()
    {
        var folder = _library.CreateFolder(null, "F");
        var path = _library.SaveNew(folder, "One", Request());
        _library.SaveNew(folder, "Two", Request());

        Assert.Equal("F/Uno", _library.Rename(path, isFolder: false, "Uno"));
        Assert.Throws<IOException>(() => _library.Rename("F/Uno", isFolder: false, "Two"));
        Assert.Equal("G", _library.Rename(folder, isFolder: true, "G"));
        Assert.True(_library.Exists("G/Uno"));

        _library.Delete("G/Uno", isFolder: false);
        Assert.False(_library.Exists("G/Uno"));
        _library.Delete("G", isFolder: true);
        Assert.Empty(_library.List());
    }

    [Fact]
    public void Paths_cannot_leave_the_library()
    {
        Assert.Throws<ArgumentException>(() => _library.Load("../outside"));
        Assert.Equal("Untitled", RequestLibrary.CleanName("  ..  "));
        Assert.Equal("con_", RequestLibrary.CleanName("con"));
    }
}

public sealed class ComposerFeatureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"snoop-composer-{Guid.NewGuid():N}");
    private readonly FakeClipboard _clipboard = new();
    private TestHost _test = null!;
    private ComposerViewModel _vm = null!;

    public Task InitializeAsync()
    {
        _test = new TestHost();
        _vm = new ComposerViewModel(_test.Host, _clipboard, Path.Combine(_root, "requests"), Path.Combine(_root, "environments.json"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _test.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Pasting_curl_into_the_url_imports_it()
    {
        _vm.Url = "curl -X PUT 'https://api.test/items/1' -H 'Accept: application/json' -d '{\"a\":1}'";

        Assert.Equal("PUT", _vm.Method);
        Assert.Equal("https://api.test/items/1", _vm.Url);
        Assert.Equal("{\"a\":1}", _vm.BodyText);
        Assert.Equal(["Accept", "Content-Type"], _vm.Headers.Select(h => h.Name));
        Assert.Contains("Imported", _vm.ResultText, StringComparison.Ordinal);
        Assert.False(_vm.ResultIsError);
    }

    [Fact]
    public void Typing_curl_does_not_import()
    {
        _vm.Url = "c";
        _vm.Url = "cu";
        _vm.Url = "curl h";
        Assert.Equal("curl h", _vm.Url);
    }

    [Fact]
    public void Disabled_and_blank_header_rows_are_not_sent()
    {
        _vm.Url = "https://x.test/";
        _vm.Headers.Add(new HeaderRowViewModel("Accept", "text/plain"));
        _vm.Headers.Add(new HeaderRowViewModel("X-Off", "1", enabled: false));
        _vm.Headers.Add(new HeaderRowViewModel());

        var request = _vm.BuildRequest(out var error);

        Assert.Null(error);
        Assert.Equal("text/plain", request!.Headers.Get("Accept"));
        Assert.False(request.Headers.Contains("X-Off"));
        Assert.Equal("Headers (1)", _vm.HeadersTitle);
        Assert.Equal("Accept: text/plain\n# X-Off: 1", _vm.HeadersText);
    }

    [Fact]
    public void Bulk_edit_round_trips_through_the_table()
    {
        _vm.Headers.Add(new HeaderRowViewModel("A", "1"));
        _vm.IsBulkEdit = true;
        _vm.HeadersText = "A: 2\n# B: 3\n";
        _vm.IsBulkEdit = false;

        Assert.Equal(2, _vm.Headers.Count);
        Assert.Equal("2", _vm.Headers[0].Value);
        Assert.False(_vm.Headers[1].Enabled);

        _vm.IsBulkEdit = true;
        _vm.HeadersText = "not a header";
        _vm.IsBulkEdit = false; // refused: fix the text first
        Assert.True(_vm.IsBulkEdit);
        Assert.True(_vm.ResultIsError);
    }

    [Fact]
    public void Variables_fill_url_headers_and_body()
    {
        _vm.Environments.BeginEdit();
        _vm.Environments.EditText = "token = t0\n[Dev]\nbase = http://dev.test:8080";
        Assert.True(_vm.Environments.ApplyEdit());
        _vm.Url = "{{base}}/users";
        _vm.Headers.Add(new HeaderRowViewModel("Authorization", "Bearer {{token}}"));
        _vm.BodyText = "{\"t\":\"{{token}}\"}";

        var request = _vm.BuildRequest(out var error);

        Assert.Null(error);
        Assert.Equal(new Uri("http://dev.test:8080/users"), request!.Url);
        Assert.Equal("Bearer t0", request.Headers.Get("Authorization"));
        Assert.Equal("{\"t\":\"t0\"}", Encoding.UTF8.GetString(request.Body!));

        _vm.Environments.SelectedIndex = 0;
        Assert.Null(_vm.BuildRequest(out error));
        Assert.Contains("{{base}}", error, StringComparison.Ordinal);
        Assert.Contains("No environment", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_as_curl_fills_variables()
    {
        _vm.Environments.BeginEdit();
        _vm.Environments.EditText = "host = api.test";
        Assert.True(_vm.Environments.ApplyEdit());
        _vm.Method = "DELETE";
        _vm.Url = "https://{{host}}/a";

        _vm.CopyAsCurlCommand.Execute(null);

        Assert.StartsWith("curl -X DELETE 'https://api.test/a'", _clipboard.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_open_rename_and_delete_saved_requests()
    {
        var asked = 0;
        _vm.SaveAsRequested += (_, _) => asked++;
        _vm.Method = "GET";
        _vm.Url = "https://api.test/users/7";
        _vm.Headers.Add(new HeaderRowViewModel("Accept", "*/*", enabled: false));

        Assert.Equal("GET 7", _vm.SuggestedName());
        _vm.SaveCommand.Execute(null);
        Assert.Equal(1, asked); // never saved: ask for a name

        _vm.Library.NewFolder(null, "Users");
        Assert.True(_vm.SaveAs("Get user", "Users"));
        Assert.Equal("Users/Get user", _vm.CurrentPath);
        Assert.Equal("Get user", _vm.Title);
        Assert.False(_vm.IsModified);

        _vm.Url = "https://api.test/users/8";
        Assert.Equal("Get user — Edited", _vm.Title);
        _vm.SaveCommand.Execute(null);
        Assert.Equal(1, asked);
        Assert.False(_vm.IsModified);

        _vm.NewRequestCommand.Execute(null);
        Assert.Equal(ComposerViewModel.UntitledName, _vm.Title);
        var folder = Assert.Single(_vm.Library.Items);
        var saved = Assert.Single(folder.Children);
        Assert.True(_vm.Open(saved));
        Assert.Equal("https://api.test/users/8", _vm.Url);
        Assert.False(_vm.Headers[0].Enabled);
        Assert.False(_vm.IsModified);

        Assert.True(_vm.Library.Rename(folder, "People"));
        Assert.Equal("People/Get user", _vm.CurrentPath);

        Assert.True(_vm.Library.Delete(Assert.Single(_vm.Library.Items)));
        Assert.Null(_vm.CurrentPath);
        Assert.True(_vm.Library.IsEmpty);
        Assert.Equal("https://api.test/users/8", _vm.Url); // the text stays, unsaved
    }

    [Fact]
    public void Folders_stay_open_and_saving_reveals_the_request()
    {
        _vm.Library.NewFolder(null, "A");
        _vm.Library.NewFolder(null, "B");
        var b = _vm.Library.Items.Single(i => i.Name == "B");
        b.IsExpanded = true;

        _vm.Url = "https://x.test/";
        Assert.True(_vm.SaveAs("In A", "A"));

        Assert.True(_vm.Library.Items.Single(i => i.Name == "A").IsExpanded); // revealed
        Assert.True(_vm.Library.Items.Single(i => i.Name == "B").IsExpanded); // kept open
        Assert.True(_vm.Library.Rename(_vm.Library.Items.Single(i => i.Name == "B"), "C"));
        Assert.True(_vm.Library.Items.Single(i => i.Name == "C").IsExpanded);
    }

    [Fact]
    public void Table_text_echo_changes_nothing()
    {
        _vm.Headers.Add(new HeaderRowViewModel("a", "b"));
        var row = _vm.Headers[0];
        _vm.HeadersText = _vm.HeadersText;
        Assert.Same(row, _vm.Headers[0]);
    }
}

public sealed class ColoredBodyTests
{
    private static MessageViewModel Show(string contentType, string body)
    {
        var vm = new MessageViewModel("none");
        var headers = new Snoop.Core.Http.HttpHeaderList();
        headers.Add("Content-Type", contentType);
        vm.Load(headers, Snoop.Core.Bodies.CapturedBody.FromBytes(Encoding.UTF8.GetBytes(body)), "raw", []);
        return vm;
    }

    [Fact]
    public void Json_xml_and_html_are_coloured()
    {
        Assert.True(Show("application/json", "{\"a\":1}").HasColoredBody);
        Assert.True(Show("application/xml", "<a>1</a>").HasColoredBody);
        Assert.True(Show("text/html", "<p>hi</p>").HasColoredBody);
        var json = Show("application/json", "{\"a\":1}");
        Assert.False(json.IsPlainTextBody);
        Assert.Contains(json.BodySpans!, s => s.Kind == Snoop.Inspection.Formatting.SyntaxKind.Key);
    }

    [Fact]
    public void Other_text_hex_and_long_bodies_stay_plain()
    {
        Assert.False(Show("text/plain", "hello").HasColoredBody);
        Assert.True(Show("text/plain", "hello").IsPlainTextBody);

        var json = Show("application/json", "{\"a\":1}");
        json.BodyMode = Snoop.Inspection.Bodies.BodyViewMode.Hex;
        Assert.False(json.HasColoredBody);
        Assert.True(json.IsPlainTextBody);

        var big = "[" + string.Join(",", Enumerable.Repeat("12345", 30_000)) + "]";
        Assert.False(Show("application/json", big).HasColoredBody);
    }
}
