using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Snoop.Core.Diagnostics;
using Snoop.Presentation.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Snoop.App.Services;

/// <summary>Runs view-model work on the window's UI thread.</summary>
internal sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        var timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new TimerRegistration(timer);
    }

    private sealed class TimerRegistration(DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

/// <summary>The Windows clipboard.</summary>
internal sealed class WinUiClipboard : IClipboardService
{
    public void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush(); // Keep the text after Snoop closes.
    }
}

/// <summary>The standard Save As dialog, attached to the main window.</summary>
internal sealed class WinUiFileSaveService(Window owner) : IFileSaveService
{
    public async Task<Stream?> PickSaveFileAsync(string suggestedName, string fileTypeName, string extension)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };
        picker.FileTypeChoices.Add(fileTypeName, [extension]);

        // Unpackaged apps must tell the picker which window owns it.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        var stream = await file.OpenStreamForWriteAsync();
        stream.SetLength(0);
        return stream;
    }
}

/// <summary>The standard Open dialog, attached to the main window.</summary>
internal sealed class WinUiFileOpenService(Window owner) : IFileOpenService
{
    public async Task<string?> PickFileAsync(string fileTypeName, params string[] extensions)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };

        foreach (var extension in extensions.Length == 0 ? new[] { "*" } : extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        // Unpackaged apps must tell the picker which window owns it.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}

/// <summary>Sends engine log messages to the debugger output.</summary>
internal sealed class DebugLogger : ISnoopLogger
{
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Info;

    public void Log(LogLevel level, string message, Exception? exception = null) =>
        System.Diagnostics.Debug.WriteLine($"[Snoop {level}] {message}{(exception is null ? string.Empty : " " + exception)}");
}
