namespace ScriptConsole.Core;

/// <summary>pwsh = PowerShell 7, powershell = Windows PowerShell 5.1.</summary>
public static class Engines
{
    public static string? Resolve(string name)
    {
        if (name == "pwsh")
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), "pwsh.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            return File.Exists(programFiles) ? programFiles : null;
        }
        var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windows) ? windows : null;
    }

    public static string Label(string name) => name == "pwsh" ? "PowerShell 7" : "Windows PowerShell 5.1";
}
