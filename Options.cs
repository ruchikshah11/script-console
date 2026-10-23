namespace ScriptConsole;

/// <summary>Command-line options.</summary>
public sealed class Options
{
    public string? Root { get; private set; }
    public string? Engine { get; private set; }
    public bool Help { get; private set; }
    /// <summary>Writes what the parser reads from every script under a folder (JSON lines) and exits.</summary>
    public string? Dump { get; private set; }
    /// <summary>Hidden: drive the window, save screenshots + results to this folder, then exit.</summary>
    public string? SelfTest { get; private set; }

    public const string Usage = """
        Usage: ScriptConsole [folder] [--root <folder>] [--engine pwsh|powershell]

          folder, --root   Open this folder straight away (skips the folder page).
          --engine         Default engine: pwsh (PowerShell 7) or powershell (5.1).
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {a}.");
            switch (a.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?": o.Help = true; break;
                case "-r" or "--root": o.Root = Next(); break;
                case "-e" or "--engine":
                    o.Engine = Next().ToLowerInvariant();
                    if (o.Engine is not ("pwsh" or "powershell")) throw new ArgumentException("--engine must be pwsh or powershell.");
                    break;
                case "--dump": o.Dump = Next(); break;
                case "--self-test": o.SelfTest = Next(); break;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"Unknown option {a}.");
                    o.Root = a;
                    break;
            }
        }
        return o;
    }
}
