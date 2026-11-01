using System.Text.Json;
using System.Text.Json.Serialization;
using Snoop.Core;

namespace Snoop.Presentation.Composer;

/// <summary>A header of a saved request. Disabled headers are kept but not sent.</summary>
public sealed record SavedHeader(string Name, string Value, bool Enabled = true);

/// <summary>A Composer request saved to disk. Placeholders such as <c>{{baseUrl}}</c> are stored as written.</summary>
public sealed record SavedRequest
{
    public string Method { get; init; } = "GET";

    public string Url { get; init; } = string.Empty;

    public IReadOnlyList<SavedHeader> Headers { get; init; } = [];

    public string Body { get; init; } = string.Empty;
}

/// <summary>A folder or a saved request. <see cref="Path"/> is relative to the library, with '/' between folders.</summary>
public sealed record LibraryEntry(string Path, string Name, bool IsFolder, IReadOnlyList<LibraryEntry> Children);

/// <summary>
/// Saved Composer requests: one JSON file per request in <c>%APPDATA%\Snoop\requests\</c>, with
/// sub-folders as folders. A request's name is its file name.
/// </summary>
public sealed class RequestLibrary
{
    public const string FileExtension = ".json";

    private const int MaxNameLength = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _root;

    public RequestLibrary(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = System.IO.Path.GetFullPath(root);
    }

    public static string DefaultDirectory => System.IO.Path.Combine(SnoopPaths.AppDataDirectory, "requests");

    public string Root => _root;

    /// <summary>A name that is safe as a file name: no path characters, not empty, not too long.</summary>
    public static string CleanName(string? name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var chars = (name ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray();
        var clean = new string(chars).Trim().TrimEnd('.').Trim();
        if (clean.Length > MaxNameLength)
        {
            clean = clean[..MaxNameLength].Trim();
        }

        if (clean.Length == 0 || clean.All(c => c == '.'))
        {
            clean = "Untitled";
        }

        return ReservedNames.Contains(clean) ? clean + "_" : clean;
    }

    /// <summary>Everything in the library: folders first, then requests, each sorted by name.</summary>
    public IReadOnlyList<LibraryEntry> List() => Directory.Exists(_root) ? ListFolder(_root) : [];

    /// <summary>Every folder's path, for "save into" choices.</summary>
    public IReadOnlyList<string> FolderPaths()
    {
        var paths = new List<string>();
        void Walk(IEnumerable<LibraryEntry> entries)
        {
            foreach (var entry in entries.Where(e => e.IsFolder))
            {
                paths.Add(entry.Path);
                Walk(entry.Children);
            }
        }

        Walk(List());
        return paths;
    }

    /// <exception cref="InvalidDataException">The file is not a saved request.</exception>
    public SavedRequest Load(string path)
    {
        var file = RequestFile(path);
        try
        {
            var request = JsonSerializer.Deserialize<SavedRequest>(File.ReadAllText(file), JsonOptions)
                          ?? throw new InvalidDataException($"{path} is empty.");
            return request with
            {
                Method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method,
                Url = request.Url ?? string.Empty,
                Body = request.Body ?? string.Empty,
                Headers = (request.Headers ?? []).Where(h => h is not null && !string.IsNullOrEmpty(h.Name))
                    .Select(h => h with { Value = h.Value ?? string.Empty }).ToList(),
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The saved request {path} is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>Saves a new request named <paramref name="name"/> in <paramref name="folder"/> (null: the top level).</summary>
    /// <returns>Its path. When the name is taken, " 2", " 3"... is added.</returns>
    public string SaveNew(string? folder, string name, SavedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var directory = FolderDirectory(folder);
        Directory.CreateDirectory(directory);
        var unique = UniqueName(directory, CleanName(name), isFolder: false);
        var path = Combine(folder, unique);
        Write(RequestFile(path), request);
        return path;
    }

    /// <summary>Replaces the request at <paramref name="path"/> (creating it when it is gone).</summary>
    public void Overwrite(string path, SavedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var file = RequestFile(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        Write(file, request);
    }

    /// <summary>Renames a request or a folder. Returns its new path.</summary>
    /// <exception cref="IOException">Something with that name already exists.</exception>
    public string Rename(string path, bool isFolder, string newName)
    {
        var clean = CleanName(newName);
        var parent = ParentOf(path);
        var newPath = Combine(parent, clean);
        if (string.Equals(newPath, path, StringComparison.Ordinal))
        {
            return path;
        }

        if (isFolder)
        {
            var from = FolderDirectory(path);
            var to = FolderDirectory(newPath);
            if (Directory.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"There is already a folder called \"{clean}\".");
            }

            MoveDirectory(from, to);
        }
        else
        {
            var from = RequestFile(path);
            var to = RequestFile(newPath);
            if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"There is already a request called \"{clean}\".");
            }

            File.Move(from, to, overwrite: string.Equals(from, to, StringComparison.OrdinalIgnoreCase));
        }

        return newPath;
    }

    /// <summary>Copies a request next to itself as "Name copy". Returns the copy's path.</summary>
    public string Duplicate(string path)
    {
        var directory = FolderDirectory(ParentOf(path));
        var name = UniqueName(directory, CleanName(NameOf(path) + " copy"), isFolder: false);
        var copy = Combine(ParentOf(path), name);
        File.Copy(RequestFile(path), RequestFile(copy));
        return copy;
    }

    /// <summary>Deletes a request, or a folder with everything in it.</summary>
    public void Delete(string path, bool isFolder)
    {
        if (isFolder)
        {
            var directory = FolderDirectory(path);
            if (string.Equals(directory, _root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The library itself cannot be deleted.");
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        else
        {
            var file = RequestFile(path);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Creates a folder (a unique name when taken) in <paramref name="parent"/>. Returns its path.</summary>
    public string CreateFolder(string? parent, string name)
    {
        var directory = FolderDirectory(parent);
        Directory.CreateDirectory(directory);
        var unique = UniqueName(directory, CleanName(name), isFolder: true);
        var path = Combine(parent, unique);
        Directory.CreateDirectory(FolderDirectory(path));
        return path;
    }

    public bool Exists(string path) => File.Exists(RequestFile(path));

    public static string NameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    public static string? ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? null : path[..slash];
    }

    private static string Combine(string? folder, string name) => string.IsNullOrEmpty(folder) ? name : $"{folder}/{name}";

    private static void Write(string file, SavedRequest request)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(request, JsonOptions));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Moves a directory; a change of case only goes through a temporary name.</summary>
    private static void MoveDirectory(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var temp = from + ".renaming";
            Directory.Move(from, temp);
            Directory.Move(temp, to);
        }
        else
        {
            Directory.Move(from, to);
        }
    }

    private static string UniqueName(string directory, string name, bool isFolder)
    {
        bool Taken(string candidate) => isFolder
            ? Directory.Exists(System.IO.Path.Combine(directory, candidate))
            : File.Exists(System.IO.Path.Combine(directory, candidate + FileExtension));

        if (!Taken(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!Taken(candidate))
            {
                return candidate;
            }
        }
    }

    private List<LibraryEntry> ListFolder(string directory)
    {
        var folders = Directory.EnumerateDirectories(directory)
            .Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))
            .OrderBy(d => System.IO.Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new LibraryEntry(Relative(d), System.IO.Path.GetFileName(d), IsFolder: true, ListFolder(d)));
        var requests = Directory.EnumerateFiles(directory, "*" + FileExtension)
            .OrderBy(f => System.IO.Path.GetFileNameWithoutExtension(f), StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new LibraryEntry(Relative(f)[..^FileExtension.Length], System.IO.Path.GetFileNameWithoutExtension(f), IsFolder: false, []));
        return [.. folders, .. requests];
    }

    private string Relative(string full) => System.IO.Path.GetRelativePath(_root, full).Replace('\\', '/');

    private string FolderDirectory(string? folder) => Resolve(folder ?? string.Empty);

    private string RequestFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Resolve(path) + FileExtension;
    }

    /// <summary>Turns a library path into a full path, refusing anything outside the library.</summary>
    private string Resolve(string path)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var root = _root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? _root : _root + System.IO.Path.DirectorySeparatorChar;
        if (!string.Equals(full, _root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"\"{path}\" is outside the request library.", nameof(path));
        }

        return full;
    }
}
