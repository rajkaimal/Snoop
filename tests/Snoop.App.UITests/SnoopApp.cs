using System.Diagnostics;
using System.Runtime.CompilerServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using FlaUIApplication = FlaUI.Core.Application;

namespace Snoop.App.UITests;

/// <summary>
/// Starts Snoop.exe in test mode (no capture, no system proxy, data in a temporary folder) with the
/// demo snapshot open, and offers small helpers for the tests. One per test; disposing closes the app.
/// </summary>
public sealed class SnoopApp : IDisposable
{
    public const int DemoSessionCount = 15;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FlaUIApplication _app;
    private readonly string _dataDirectory;

    public SnoopApp()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "Snoop-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        var start = new ProcessStartInfo(FindExe())
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(FindExe())!,
        };
        start.ArgumentList.Add("--test");
        start.ArgumentList.Add("--open");
        start.ArgumentList.Add(Path.Combine(RepoRoot(), "docs", "demo.snoop"));
        start.Environment["SNOOP_DATA_DIR"] = _dataDirectory;

        _app = FlaUIApplication.Launch(start);
        Automation = new UIA3Automation();
        try
        {
            Window = _app.GetMainWindow(Automation, TimeSpan.FromSeconds(60))
                     ?? throw new InvalidOperationException("Snoop's window did not appear.");
            WaitUntil(() => CountText == $"{DemoSessionCount} sessions", "the demo sessions to load", TimeSpan.FromSeconds(60));
            Focus();
        }
        catch
        {
            // xunit never disposes a test class whose constructor failed: don't leave Snoop running.
            if (!_app.HasExited)
            {
                _app.Kill();
            }

            _app.Dispose();
            Automation.Dispose();
            TryDelete(_dataDirectory);
            throw;
        }
    }

    public UIA3Automation Automation { get; }

    public Window Window { get; }

    /// <summary>The status bar's count, e.g. "15 sessions" or "6 of 15 sessions".</summary>
    public string CountText => ById("CountText")?.Name ?? string.Empty;

    public TextBox SearchBox => ById("SearchBox")?.AsTextBox() ?? throw new InvalidOperationException("No search box.");

    /// <summary>The rows of the session list, in the order shown.</summary>
    public IReadOnlyList<ListBoxItem> Rows =>
        ById("SessionList")?.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Select(e => e.AsListBoxItem()).ToList() ?? [];

    /// <summary>Indexes of the selected rows.</summary>
    public IReadOnlyList<int> SelectedRows => Rows.Select((row, index) => (row, index)).Where(r => r.row.IsSelected).Select(r => r.index).ToList();

    /// <summary>The title of the open tool ("Composer", "Rules"...), or null while Inspect is shown.</summary>
    public string? ToolTitle => ById("ToolTitle")?.Name;

    /// <summary>
    /// Everything the info bar under the toolbar says. Its message text sits in the raw UI Automation view
    /// (not the control view FindAllDescendants searches), so this walks the raw tree.
    /// </summary>
    public string MessageText
    {
        get
        {
            var bar = ById("MessageBar");
            if (bar is null)
            {
                return string.Empty;
            }

            var names = new List<string>();
            var walker = Automation.TreeWalkerFactory.GetRawViewWalker();
            void Walk(AutomationElement element, int depth)
            {
                names.Add(element.Properties.Name.ValueOrDefault ?? string.Empty);
                if (depth > 8)
                {
                    return;
                }

                for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
                {
                    Walk(child, depth + 1);
                }
            }

            Walk(bar, 0);
            return string.Join(" | ", names.Where(n => n.Length > 0));
        }
    }

    /// <summary>The state the search tests look at, for failure messages.</summary>
    public string Describe() => $"count \"{CountText}\", search \"{SearchBox.Text}\", selected rows [{string.Join(", ", SelectedRows)}], focus \"{FocusedId}\"";

    public string? FocusedId => Automation.FocusedElement()?.AutomationId;

    public AutomationElement? ById(string id) => Window.FindFirstDescendant(cf => cf.ByAutomationId(id));

    public void Focus()
    {
        Window.SetForeground();
        Window.Focus();
        Wait.UntilInputIsProcessed();
    }

    /// <summary>Presses keys together, e.g. Press(Keys.Control, VirtualKeyShort.KEY_L).</summary>
    public static void Press(params VirtualKeyShort[] keys)
    {
        Keyboard.TypeSimultaneously(keys);
        Wait.UntilInputIsProcessed();
        Thread.Sleep(150); // let the app's dispatcher react
    }

    public static void Type(string text)
    {
        Keyboard.Type(text);
        Wait.UntilInputIsProcessed();
        Thread.Sleep(300); // the list refilters after typing
    }

    /// <summary>
    /// Selects a row as a click would (with <paramref name="control"/>, adds it to the selection) and puts
    /// the keyboard focus on it. Uses UI Automation rather than the mouse, so display scaling and window
    /// position cannot make it miss.
    /// </summary>
    public void ClickRow(int index, bool control = false)
    {
        var row = Rows[index];
        if (control)
        {
            row.AddToSelection();
        }
        else
        {
            row.Select();
        }

        row.Focus();
        Wait.UntilInputIsProcessed();
        WaitUntil(() => Rows[index].IsSelected, $"row {index} to be selected");
        Thread.Sleep(150);
    }

    /// <summary>Waits until <paramref name="condition"/> holds, or fails the test saying what was expected.</summary>
    public static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null, Func<string>? actual = null)
    {
        var limit = timeout ?? Timeout;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentOutOfRangeException
                                       or System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.FlaUIException)
            {
                // The UI was changing under us; try again.
            }

            Thread.Sleep(100);
        }

        string seen;
        try
        {
            seen = actual?.Invoke() ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.FlaUIException)
        {
            seen = $"(could not read the app's state: {ex.Message})";
        }

        Assert.Fail(seen.Length == 0 ? $"Timed out waiting for {what}." : $"Timed out waiting for {what}. Saw: {seen}");
    }

    /// <summary>The clipboard's text (read on an STA thread, as Windows requires).</summary>
    public static string ClipboardText()
    {
        string text = string.Empty;
        var thread = new Thread(() =>
        {
            try
            {
                text = System.Windows.Forms.Clipboard.GetText();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Snoop may be writing it right now; the caller polls again.
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static void ClearClipboard()
    {
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    public void Dispose()
    {
        try
        {
            // A normal close: Snoop stops (nothing is captured in test mode) and exits.
            _app.Close();
            var watch = Stopwatch.StartNew();
            while (!_app.HasExited && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(100);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            if (!_app.HasExited)
            {
                _app.Kill();
            }

            _app.Dispose();
            Automation.Dispose();
            TryDelete(_dataDirectory);
        }
    }

    private static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static string RepoRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));

    /// <summary>SNOOP_EXE, or the newest Snoop.exe built under src/Snoop.App/bin.</summary>
    private static string FindExe()
    {
        if (Environment.GetEnvironmentVariable("SNOOP_EXE") is { Length: > 0 } configured)
        {
            return configured;
        }

        var bin = Path.Combine(RepoRoot(), "src", "Snoop.App", "bin");
        var exe = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "Snoop.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return exe ?? throw new InvalidOperationException(
            "Snoop.exe was not found. Build the app first (dotnet build src/Snoop.App -c Debug -p:Platform=x64) or set SNOOP_EXE.");
    }
}
