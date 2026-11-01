namespace Snoop.Presentation.Services;

/// <summary>Runs work on the UI thread. Implemented by the app with its DispatcherQueue.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);

    /// <summary>Calls <paramref name="tick"/> on the UI thread every <paramref name="interval"/> until disposed.</summary>
    IDisposable StartTimer(TimeSpan interval, Action tick);
}

/// <summary>Puts text on the clipboard.</summary>
public interface IClipboardService
{
    void SetText(string text);
}

/// <summary>Asks the user where to save a file.</summary>
public interface IFileSaveService
{
    /// <summary>Shows a save dialog. Returns a writable stream, or null if the user cancelled.</summary>
    Task<Stream?> PickSaveFileAsync(string suggestedName, string fileTypeName, string extension);
}

/// <summary>Asks the user to choose an existing file.</summary>
public interface IFileOpenService
{
    /// <summary>Shows an open dialog. Returns the chosen file's full path, or null if the user cancelled.</summary>
    /// <param name="fileTypeName">Shown in the dialog, e.g. "Snoop snapshot".</param>
    /// <param name="extensions">Allowed extensions such as ".snoop"; "*" allows any file.</param>
    Task<string?> PickFileAsync(string fileTypeName, params string[] extensions);
}
