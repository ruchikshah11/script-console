namespace ScriptConsole.Core;

/// <summary>One .ps1 file found under the loaded folder.</summary>
public sealed record ScriptEntry(string Rel, string Full, string Name, string Folder, string Dir)
{
    /// <summary>Lower-case relative path, used for search.</summary>
    public string Lower { get; } = Rel.ToLowerInvariant();
}

/// <summary>A parameter read from the script's param() block (and its comment-based help).</summary>
public sealed class ParamInfo
{
    static readonly Regex SecretName = new("pass|secret|token|pwd|apikey|clientkey", RegexOptions.IgnoreCase);

    public string Name { get; init; } = "";
    public string TypeName { get; init; } = "Object";
    public bool IsSwitch { get; init; }
    public bool IsBool { get; init; }
    public bool IsSecure { get; init; }
    public bool IsCredential { get; init; }
    public bool IsArray { get; init; }
    public bool Mandatory { get; init; }
    public string Default { get; init; } = "";
    public List<string> ValidSet { get; init; } = [];
    public string Description { get; set; } = "";

    /// <summary>Value is masked on screen and never saved.</summary>
    public bool IsSecret => IsSecure || SecretName.IsMatch(Name);
}

/// <summary>Everything shown about a script; read without running it.</summary>
public sealed class ScriptInfo
{
    public DateTime Stamp { get; init; }
    public string Synopsis { get; set; } = "";
    public string Description { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Examples { get; } = [];
    public List<string> Links { get; } = [];
    /// <summary>Leading # comments, used when the script has no comment-based help.</summary>
    public List<string> Comments { get; } = [];
    public List<ParamInfo> Params { get; } = [];
    /// <summary>"Core" or "Desktop" from #Requires -PSEdition, else empty.</summary>
    public string Edition { get; set; } = "";
}
