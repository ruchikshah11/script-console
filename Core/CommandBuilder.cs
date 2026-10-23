namespace ScriptConsole.Core;

/// <summary>The command as shown (secrets masked), the child splat lines, and missing required parameters.</summary>
public sealed record RunArgs(string Display, IReadOnlyList<string> Splat, IReadOnlyList<string> Missing, string Extra);

public static class CommandBuilder
{
    public static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    static string Lit(IEnumerable<string> values) => "@(" + string.Join(", ", values.Select(Lit)) + ")";

    public static RunArgs Build(ScriptEntry entry, ScriptInfo info, IReadOnlyDictionary<string, object> values)
    {
        var display = new List<string> { $"& '.\\{entry.Rel}'" };
        var splat = new List<string>();
        var missing = new List<string>();
        foreach (var p in info.Params)
        {
            if (p.IsCredential) continue;
            values.TryGetValue(p.Name, out var v);
            var key = Lit(p.Name);
            if (p.IsSwitch)
            {
                if (v is true) { display.Add("-" + p.Name); splat.Add($"$__a[{key}] = $true"); }
                continue;
            }
            if (p.IsBool)
            {
                bool on = v is true;
                if (on || p.Mandatory)
                {
                    var lit = on ? "$true" : "$false";
                    display.Add($"-{p.Name}:{lit}");
                    splat.Add($"$__a[{key}] = {lit}");
                }
                continue;
            }
            var text = v as string ?? "";
            if (text.Length == 0)
            {
                if (p.Mandatory) missing.Add(p.Name);
                continue;
            }
            var valueLit = p.IsArray ? Lit(text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)) : Lit(text);
            display.Add($"-{p.Name} {(p.IsSecret ? "'****'" : valueLit)}");
            splat.Add(p.IsSecure
                ? $"$__a[{key}] = ConvertTo-SecureString {Lit(text)} -AsPlainText -Force"
                : $"$__a[{key}] = {valueLit}");
        }
        var extra = ((values.TryGetValue("__extra", out var x) ? x as string : null) ?? "").Trim();
        if (extra.Length > 0) display.Add(extra);
        return new RunArgs(string.Join(" ", display), splat, missing, extra);
    }

    /// <summary>
    /// Child process script: run from the script's folder with the values splatted; exit code preserved.
    /// <paramref name="capture"/> writes every stream to stdout as text, prefixing error / warning /
    /// verbose lines with ~E~ / ~W~ / ~V~ so the output panel can colour them.
    /// <paramref name="pauseAtEnd"/> keeps a console window open until Enter.
    /// </summary>
    public static string ChildScript(ScriptEntry entry, RunArgs run, bool capture = false, bool pauseAtEnd = false)
    {
        var call = $"& {Lit(entry.Full)} @__a {run.Extra}";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("Set-Location -LiteralPath " + Lit(entry.Dir));
        sb.AppendLine("$__a = @{}");
        foreach (var line in run.Splat) sb.AppendLine(line);
        sb.AppendLine("$__ok = $true");
        if (capture)
        {
            sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            sb.AppendLine("if (Get-Variable PSStyle -ErrorAction SilentlyContinue) { $PSStyle.OutputRendering = 'PlainText' }");
            sb.AppendLine("try { [Console]::OutputEncoding = New-Object Text.UTF8Encoding $false } catch { }");
            sb.AppendLine("""
                function __Format-Error($r) {
                    $m = 'ERROR: ' + $r.Exception.Message
                    if ($r.InvocationInfo -and $r.InvocationInfo.ScriptName) { $m += '  (' + (Split-Path $r.InvocationInfo.ScriptName -Leaf) + ':' + $r.InvocationInfo.ScriptLineNumber + ')' }
                    $m -split "\r?\n" | ForEach-Object { '~E~' + $_ }
                }
                """);
            sb.AppendLine("try {");
            sb.AppendLine($"    & {{ {call} }} *>&1 | ForEach-Object {{");
            sb.AppendLine("""
                        if ($_ -is [System.Management.Automation.ErrorRecord]) { __Format-Error $_ }
                        elseif ($_ -is [System.Management.Automation.WarningRecord]) { '~W~WARNING: ' + $_.Message }
                        elseif ($_ -is [System.Management.Automation.VerboseRecord]) { '~V~VERBOSE: ' + $_.Message }
                        elseif ($_ -is [System.Management.Automation.DebugRecord]) { '~V~DEBUG: ' + $_.Message }
                        else { $_ }
                    } | Out-String -Stream -Width 240 | ForEach-Object { [Console]::Out.WriteLine($_) }
                } catch {
                    __Format-Error $_ | ForEach-Object { [Console]::Out.WriteLine($_) }
                    $__ok = $false
                }
                [Console]::Out.Flush()
                """);
        }
        else
        {
            sb.AppendLine($"try {{ {call}; $__ok = $? }} catch {{ Write-Host ($_ | Out-String) -ForegroundColor Red; $__ok = $false }}");
        }
        sb.AppendLine("$__c = 0; if ($LASTEXITCODE) { $__c = $LASTEXITCODE } elseif (-not $__ok) { $__c = 1 }");
        if (pauseAtEnd)
        {
            sb.AppendLine("$__color = if ($__c -eq 0) { 'Green' } else { 'Red' }");
            sb.AppendLine("Write-Host (\"`n---- finished with exit code {0} ----\" -f $__c) -ForegroundColor $__color");
            sb.AppendLine("Read-Host 'Press Enter to close' | Out-Null");
        }
        sb.AppendLine("exit $__c");
        return sb.ToString();
    }

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
