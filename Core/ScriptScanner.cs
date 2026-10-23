namespace ScriptConsole.Core;

public static class ScriptScanner
{
    public static readonly string[] DefaultExclude = ["Logs", "bin", "obj", "node_modules", ".git", ".vs", "packages"];

    static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
    };

    /// <summary>Every .ps1 under <paramref name="root"/>, root files first, then by folder and name.</summary>
    public static List<ScriptEntry> Scan(string root, IReadOnlyCollection<string> exclude)
    {
        var list = new List<ScriptEntry>();
        foreach (var full in Directory.EnumerateFiles(root, "*.ps1", Recursive))
        {
            if (!full.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(root, full);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts[..^1].Any(p => exclude.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            var folder = Path.GetDirectoryName(rel);
            list.Add(new ScriptEntry(rel, full, Path.GetFileNameWithoutExtension(full),
                string.IsNullOrEmpty(folder) ? "(root)" : folder, Path.GetDirectoryName(full)!));
        }
        return list
            .OrderBy(e => e.Folder == "(root)" ? 0 : 1)
            .ThenBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Number of .ps1 files under a folder, stopping at <paramref name="cap"/>.</summary>
    public static int Count(string root, int cap = 5000)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.ps1", Recursive)
                .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                .Take(cap)
                .Count();
        }
        catch { return 0; }
    }
}
