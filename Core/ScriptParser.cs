namespace ScriptConsole.Core;

/// <summary>
/// Reads comment-based help, leading comments, #Requires -PSEdition and the param() block of a
/// PowerShell script without running it. A small lexer skips strings (incl. here-strings) and
/// comments so brackets and commas inside them don't confuse the parameter split.
/// </summary>
public static class ScriptParser
{
    static readonly Regex RequiresEdition = new(@"^\s*#requires\b[^\r\n]*-PSEdition\s+(Core|Desktop)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    static readonly Regex HelpKeyword = new(
        @"^\s*\.(SYNOPSIS|DESCRIPTION|PARAMETER|EXAMPLE|INPUTS|OUTPUTS|NOTES|LINK|COMPONENT|ROLE|FUNCTIONALITY|FORWARDHELPTARGETNAME|FORWARDHELPCATEGORY|REMOTEHELPRUNSPACE|EXTERNALHELP)\b[ \t]*(.*)$",
        RegexOptions.IgnoreCase);

    static readonly Regex MandatoryArg = new(@"\bMandatory\b\s*(?:=\s*([^,)\s]+))?", RegexOptions.IgnoreCase);
    static readonly Regex HelpMessageArg = new(@"\bHelpMessage\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static readonly Regex QuotedString = new(@"'((?:[^']|'')*)'|""((?:[^""`]|`.|"""")*)""");

    static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = "String", ["int"] = "Int32", ["int32"] = "Int32", ["long"] = "Int64", ["int64"] = "Int64",
        ["bool"] = "Boolean", ["boolean"] = "Boolean", ["switch"] = "SwitchParameter", ["switchparameter"] = "SwitchParameter",
        ["securestring"] = "SecureString", ["pscredential"] = "PSCredential", ["datetime"] = "DateTime",
        ["double"] = "Double", ["decimal"] = "Decimal", ["hashtable"] = "Hashtable", ["object"] = "Object",
        ["guid"] = "Guid", ["uri"] = "Uri", ["xml"] = "XmlDocument", ["scriptblock"] = "ScriptBlock", ["char"] = "Char",
    };

    public static ScriptInfo Parse(string path)
    {
        string text = File.ReadAllText(path);
        var info = new ScriptInfo { Stamp = File.GetLastWriteTimeUtc(path) };

        var req = RequiresEdition.Match(text);
        if (req.Success) info.Edition = req.Groups[1].Value.Equals("core", StringComparison.OrdinalIgnoreCase) ? "Core" : "Desktop";

        var (groups, codeStart) = LeadingComments(text);
        string? help = groups.FirstOrDefault(HasHelpKeyword) ?? TrailingHelp(text);
        var paramHelp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (help is not null) ParseHelp(help, info, paramHelp);
        else
        {
            var lines = groups.SelectMany(g => Dedent(g.Split('\n').Select(l => l.TrimEnd('\r'))))
                .Where(l => !Regex.IsMatch(l, @"^\s*requires\b", RegexOptions.IgnoreCase))
                .ToList();
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            info.Comments.AddRange(lines);
        }

        int open = FindParamBlock(text, codeStart);
        if (open >= 0)
        {
            int close = FindClose(text, open);
            foreach (var segment in SplitTopLevel(text[(open + 1)..Math.Max(open + 1, close)]))
            {
                var p = ParseParam(segment);
                if (p is null) continue;
                if (paramHelp.TryGetValue(p.Name, out var doc) && doc.Length > 0) p.Description = doc;
                info.Params.Add(p);
            }
        }
        return info;
    }

    // ---- comments and help -------------------------------------------------------------------

    /// <summary>Block comments and runs of # lines before the first code, and where the code starts.</summary>
    static (List<string> Groups, int CodeStart) LeadingComments(string t)
    {
        var groups = new List<string>();
        StringBuilder? run = null;
        int i = 0;
        while (i < t.Length)
        {
            int newlines = 0;
            while (i < t.Length && char.IsWhiteSpace(t[i])) { if (t[i] == '\n') newlines++; i++; }
            if (run is not null && newlines > 1) { groups.Add(run.ToString()); run = null; }
            if (i >= t.Length) break;
            if (At(t, i, "<#"))
            {
                if (run is not null) { groups.Add(run.ToString()); run = null; }
                int end = t.IndexOf("#>", i + 2, StringComparison.Ordinal);
                if (end < 0) end = t.Length;
                groups.Add(t[(i + 2)..end]);
                i = Math.Min(t.Length, end + 2);
                continue;
            }
            if (t[i] == '#')
            {
                int eol = t.IndexOf('\n', i);
                if (eol < 0) eol = t.Length;
                (run ??= new StringBuilder()).Append(t[(i + 1)..eol].TrimEnd('\r')).Append('\n');
                i = eol;
                continue;
            }
            break;
        }
        if (run is not null) groups.Add(run.ToString());
        return (groups, i);
    }

    static bool HasHelpKeyword(string block) => block.Split('\n').Any(l => HelpKeyword.IsMatch(l.TrimEnd('\r')));

    /// <summary>Comment-based help may also sit in a block comment at the very end of the script.</summary>
    static string? TrailingHelp(string t)
    {
        var trimmed = t.TrimEnd();
        if (!trimmed.EndsWith("#>", StringComparison.Ordinal)) return null;
        int start = trimmed.LastIndexOf("<#", StringComparison.Ordinal);
        if (start < 0) return null;
        var block = trimmed[(start + 2)..^2];
        return HasHelpKeyword(block) ? block : null;
    }

    static void ParseHelp(string help, ScriptInfo info, Dictionary<string, string> paramHelp)
    {
        string? key = null, arg = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (key is null) return;
            var body = string.Join("\n", Dedent(buffer)).Trim();
            switch (key)
            {
                case "SYNOPSIS": info.Synopsis = body; break;
                case "DESCRIPTION": info.Description = body; break;
                case "NOTES": info.Notes = body; break;
                case "EXAMPLE": if (body.Length > 0) info.Examples.Add(body); break;
                case "LINK": info.Links.Add(string.IsNullOrWhiteSpace(arg) ? body : (arg + (body.Length > 0 ? "\n" + body : ""))); break;
                case "PARAMETER":
                    if (!string.IsNullOrWhiteSpace(arg)) paramHelp[arg.Trim()] = Regex.Replace(body, @"\s+", " ").Trim();
                    break;
            }
            buffer.Clear();
        }

        foreach (var raw in help.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = HelpKeyword.Match(line);
            if (m.Success)
            {
                Flush();
                key = m.Groups[1].Value.ToUpperInvariant();
                arg = m.Groups[2].Value.Trim();
            }
            else if (key is not null) buffer.Add(line);
        }
        Flush();
    }

    static IEnumerable<string> Dedent(IEnumerable<string> lines)
    {
        var list = lines.Select(l => l.Replace("\t", "    ")).ToList();
        int indent = list.Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart().Length)
            .DefaultIfEmpty(0).Min();
        return list.Select(l => l.Length >= indent && l[..indent].Trim().Length == 0 ? l[indent..] : l.TrimStart());
    }

    // ---- param() block --------------------------------------------------------------------------

    /// <summary>
    /// Index of the '(' after the script's param keyword, or -1. Like PowerShell, param only counts as
    /// the first statement: only comments, attributes such as [CmdletBinding()] and using lines may
    /// come before it (otherwise "Param(...)" is just a command and nothing binds).
    /// </summary>
    static int FindParamBlock(string t, int start)
    {
        int j = start;
        while (true)
        {
            j = SkipTrivia(t, j);
            if (j >= t.Length) return -1;
            if (t[j] == '[') { j = FindClose(t, j) + 1; continue; }
            if (string.Compare(t, j, "using", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
                && j + 5 < t.Length && char.IsWhiteSpace(t[j + 5]))
            {
                int eol = t.IndexOf('\n', j);
                j = eol < 0 ? t.Length : eol;
                continue;
            }
            break;
        }
        if (string.Compare(t, j, "param", 0, 5, StringComparison.OrdinalIgnoreCase) != 0) return -1;
        int k = j + 5;
        while (k < t.Length && char.IsWhiteSpace(t[k])) k++;
        return k < t.Length && t[k] == '(' ? k : -1;
    }

    static ParamInfo? ParseParam(string seg)
    {
        var attrs = new List<string>();
        int j = 0;
        while (true)
        {
            j = SkipTrivia(seg, j);
            if (j < seg.Length && seg[j] == '[')
            {
                int end = FindClose(seg, j);
                attrs.Add(seg[(j + 1)..Math.Max(j + 1, end)]);
                j = end + 1;
                continue;
            }
            break;
        }
        if (j >= seg.Length || seg[j] != '$') return null;

        int n = j + 1;
        string name;
        if (n < seg.Length && seg[n] == '{')
        {
            int end = seg.IndexOf('}', n);
            if (end < 0) return null;
            name = seg[(n + 1)..end];
            n = end + 1;
        }
        else
        {
            while (n < seg.Length && (char.IsLetterOrDigit(seg[n]) || seg[n] == '_')) n++;
            name = seg[(j + 1)..n];
        }
        if (name.Length == 0) return null;

        n = SkipTrivia(seg, n);
        string def = "";
        if (n < seg.Length && seg[n] == '=') def = Regex.Replace(StripComments(seg[(n + 1)..]), @"\s+", " ").Trim();

        string type = "";
        bool mandatory = false;
        string helpMessage = "";
        var set = new List<string>();
        foreach (var raw in attrs)
        {
            var a = raw.Trim();
            int paren = a.IndexOf('(');
            if (paren < 0) { type = a; continue; }
            var attrName = a[..paren].Trim().Split('.')[^1];
            int last = a.LastIndexOf(')');
            var args = a[(paren + 1)..(last > paren ? last : a.Length)];
            if (attrName.Equals("Parameter", StringComparison.OrdinalIgnoreCase))
            {
                var m = MandatoryArg.Match(args);
                if (m.Success) mandatory |= !m.Groups[1].Success || m.Groups[1].Value.Trim().ToLowerInvariant() is "$true" or "1";
                var h = HelpMessageArg.Match(args);
                if (h.Success) helpMessage = h.Groups[2].Value;
            }
            else if (attrName.Equals("ValidateSet", StringComparison.OrdinalIgnoreCase))
            {
                // quoted strings or bare values (e.g. ValidateSet(1031, 1033)); named arguments like IgnoreCase = $false are skipped
                foreach (var arg in SplitTopLevel(args).Select(s => s.Trim()).Where(s => s.Length > 0 && !s.Contains('=')))
                {
                    var q = QuotedString.Match(arg);
                    set.Add(q.Success && q.Index == 0
                        ? (q.Groups[1].Success ? q.Groups[1].Value.Replace("''", "'") : q.Groups[2].Value)
                        : arg);
                }
            }
        }

        var lower = type.ToLowerInvariant();
        bool isArray = lower.EndsWith("[]");
        var baseName = (isArray ? type[..^2] : type).Split('.')[^1];
        var key = baseName.ToLowerInvariant();
        var display = baseName.Length == 0 ? "Object" : TypeNames.GetValueOrDefault(key, baseName);

        return new ParamInfo
        {
            Name = name,
            TypeName = display + (isArray ? "[]" : ""),
            IsSwitch = key is "switch" or "switchparameter",
            IsBool = key is "bool" or "boolean",
            IsSecure = key == "securestring",
            IsCredential = key == "pscredential",
            IsArray = isArray,
            Mandatory = mandatory,
            Default = def,
            ValidSet = set,
            Description = helpMessage,
        };
    }

    // ---- lexing helpers ---------------------------------------------------------------------------

    static bool At(string t, int i, string s) => string.CompareOrdinal(t, i, s, 0, s.Length) == 0;

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '$' or ':' or '.';

    /// <summary>If a string or comment starts at <paramref name="i"/>, the index just after it; else <paramref name="i"/>.</summary>
    static int SkipNonCode(string t, int i)
    {
        char c = t[i];
        if (c == '<' && i + 1 < t.Length && t[i + 1] == '#')
        {
            int end = t.IndexOf("#>", i + 2, StringComparison.Ordinal);
            return end < 0 ? t.Length : end + 2;
        }
        if (c == '#')
        {
            int eol = t.IndexOf('\n', i);
            return eol < 0 ? t.Length : eol;
        }
        if (c == '@' && i + 1 < t.Length && (t[i + 1] == '\'' || t[i + 1] == '"') && IsHereStringStart(t, i))
        {
            string endMark = "\n" + t[i + 1] + "@";
            int end = t.IndexOf(endMark, i + 2, StringComparison.Ordinal);
            return end < 0 ? t.Length : end + endMark.Length;
        }
        if (c == '\'' || c == '"')
        {
            int j = i + 1;
            while (j < t.Length)
            {
                char d = t[j];
                if (c == '"' && d == '`') { j += 2; continue; }
                if (d == c)
                {
                    if (j + 1 < t.Length && t[j + 1] == c) { j += 2; continue; }
                    return j + 1;
                }
                j++;
            }
            return t.Length;
        }
        if (c == '`') return Math.Min(t.Length, i + 2);
        return i;
    }

    static bool IsHereStringStart(string t, int i)
    {
        int eol = t.IndexOf('\n', i);
        if (eol < 0) return false;
        return t[(i + 2)..eol].Trim().Length == 0;
    }

    /// <summary>Index of the bracket that closes the one at <paramref name="open"/>.</summary>
    static int FindClose(string t, int open)
    {
        int depth = 0;
        for (int j = open; j < t.Length; j++)
        {
            int skip = SkipNonCode(t, j);
            if (skip > j) { j = skip - 1; continue; }
            char c = t[j];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0) return j;
            }
        }
        return t.Length - 1;
    }

    static List<string> SplitTopLevel(string t)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int j = 0; j < t.Length; j++)
        {
            int skip = SkipNonCode(t, j);
            if (skip > j) { j = skip - 1; continue; }
            char c = t[j];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { parts.Add(t[start..j]); start = j + 1; }
        }
        parts.Add(t[start..]);
        return parts;
    }

    static int SkipTrivia(string t, int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) { i++; continue; }
            if (t[i] == '#' || (t[i] == '<' && i + 1 < t.Length && t[i + 1] == '#')) { i = SkipNonCode(t, i); continue; }
            break;
        }
        return i;
    }

    static string StripComments(string t)
    {
        var sb = new StringBuilder();
        for (int j = 0; j < t.Length; j++)
        {
            bool comment = t[j] == '#' || (t[j] == '<' && j + 1 < t.Length && t[j + 1] == '#');
            int skip = SkipNonCode(t, j);
            if (skip > j)
            {
                if (!comment) sb.Append(t, j, skip - j);
                j = skip - 1;
                continue;
            }
            sb.Append(t[j]);
        }
        return sb.ToString();
    }
}
