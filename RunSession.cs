using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScriptConsole;

/// <summary>One run of a script: its process, collected output and the row shown in History.</summary>
public sealed class RunSession : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Script { get; init; }
    public required string Command { get; init; }
    public required string FullCommand { get; init; }
    public required string Engine { get; init; }
    public required bool Captured { get; init; }
    public string Started { get; } = DateTime.Now.ToString("HH:mm:ss");
    public string Mode => Captured ? "Output panel" : "Console window";

    public Process? Process { get; set; }
    public Stopwatch Watch { get; } = Stopwatch.StartNew();
    public List<string> Lines { get; } = [];
    /// <summary>Output lines received on background threads, moved to <see cref="Lines"/> on the UI thread.</summary>
    public ConcurrentQueue<string> Pending { get; } = new();
    /// <summary>Set (from a background thread) once the process ended and all its output was read.</summary>
    public volatile bool Exited;
    public int ExitCode;
    public bool Stopped { get; set; }
    public bool Done { get; set; }

    string status = "Running";
    string statusColor = "#0969DA";
    string exit = "";
    string duration = "00:00:00";

    public string Status { get => status; set => Set(ref status, value); }
    public string StatusColor { get => statusColor; set => Set(ref statusColor, value); }
    public string Exit { get => exit; set => Set(ref exit, value); }
    public string Duration { get => duration; set => Set(ref duration, value); }

    void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Row of the search results list.</summary>
public sealed record SearchRow(string Title, string Sub, string HeaderVis, string ItemVis, bool Selectable, ScriptEntry? Entry);

/// <summary>Card on the folder page.</summary>
public sealed record SuggestionRow(string Path, string Label, string CountText, string BadgeBg, string BadgeFg, int Count);

/// <summary>Folder in the script tree.</summary>
sealed class TreeNode(string name)
{
    public string Name { get; } = name;
    public List<TreeNode> Folders { get; } = [];
    public List<ScriptEntry> Scripts { get; } = [];
    public int Total { get; set; }

    public TreeNode Child(string name)
    {
        var node = Folders.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (node is null) { node = new TreeNode(name); Folders.Add(node); }
        return node;
    }
}

static class NativeIcon
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    /// <summary>The main icon of an exe as a WPF image (used for the PowerShell 7 window icon).</summary>
    public static System.Windows.Media.ImageSource? FromExe(string path)
    {
        if (ExtractIconEx(path, 0, out var large, out var small, 1) == 0) return null;
        try
        {
            var handle = large != IntPtr.Zero ? large : small;
            var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(handle, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }
}
