using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScriptConsole;

public partial class MainWindow : Window
{
    const int MaxResults = 400;

    static readonly Brush OutText = Freeze("#E6EDF3"), OutErr = Freeze("#FF7B72"), OutWarn = Freeze("#E3B341"),
        OutVerbose = Freeze("#8B949E"), OutInfo = Freeze("#79C0FF"), OutOk = Freeze("#56D364"),
        Muted = Freeze("#656D76"), Hint = Freeze("#8C959F"), Red = Freeze("#CF222E"), Purple = Freeze("#8250DF"),
        Body = Freeze("#1F2328"), FolderIcon = Freeze("#B7791F"), ScriptIcon = Freeze("#2563EB");

    static readonly Dictionary<string, (string Bg, string Fg)> Pills = new()
    {
        ["Idle"] = ("#EEF1F4", "#57606A"), ["Running"] = ("#DDF4FF", "#0969DA"), ["Console"] = ("#FFF8C5", "#9A6700"),
        ["Succeeded"] = ("#DAFBE1", "#1A7F37"), ["Failed"] = ("#FFEBE9", "#CF222E"), ["Stopped"] = ("#EEF1F4", "#57606A"),
    };

    readonly Options options;
    readonly StateStore store = StateStore.Load();
    readonly Dictionary<string, ScriptInfo> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<string, object>> valuesByScript = new(StringComparer.OrdinalIgnoreCase);
    readonly ObservableCollection<RunSession> runs = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly Paragraph para = new() { Margin = new Thickness(0) };
    readonly List<(ParamInfo P, FrameworkElement Control)> inputs = [];

    string? root;
    List<ScriptEntry> files = [];
    ScriptEntry? entry;
    ScriptInfo? info;
    Dictionary<string, object>? vals;
    bool loading, fullHelp;
    RunSession? viewing;
    int lineCount, tick;
    string engine;

    public MainWindow(Options options)
    {
        this.options = options;
        InitializeComponent();

        engine = options.Engine ?? store.Engine ?? "pwsh";
        if (Engines.Resolve(engine) is null) engine = Engines.Resolve("pwsh") is not null ? "pwsh" : "powershell";
        var pwsh = Engines.Resolve("pwsh");
        if (pwsh is not null) Icon = NativeIcon.FromExe(pwsh);

        OutputBox.Document = new FlowDocument(para)
        {
            PagePadding = new Thickness(4, 2, 4, 2),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Foreground = OutText,
        };
        HistoryList.ItemsSource = runs;
        foreach (var name in new[] { "pwsh", "powershell" })
            if (Engines.Resolve(name) is not null)
                EngineBox.Items.Add(new ComboBoxItem { Content = Engines.Label(name), Tag = name });
        EngineBox.SelectedItem = EngineBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == engine) ?? EngineBox.Items.Cast<object>().FirstOrDefault();

        WireEvents();
        ShowRun(null);
        timer.Tick += (_, _) => OnTick();
        timer.Start();
        ContentRendered += async (_, _) =>
        {
            if (options.Root is not null) OpenFolder(options.Root);
            else await ShowPickerAsync();
            if (options.SelfTest is not null) await SelfTestAsync(options.SelfTest, Environment.GetEnvironmentVariable("SCRIPTCONSOLE_TESTLIB") ?? "");
        };
    }

    static SolidColorBrush Freeze(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    // ---- folder page ------------------------------------------------------------------------------

    async Task ShowPickerAsync()
    {
        PickerError.Visibility = Visibility.Collapsed;
        PickerBackBtn.Visibility = root is null ? Visibility.Collapsed : Visibility.Visible;
        MainPage.Visibility = Visibility.Collapsed;
        PickerPage.Visibility = Visibility.Visible;
        StatusText.Text = "Looking for script folders...";

        var recent = store.Recent.Take(3).ToList();
        var rows = await Task.Run(() =>
        {
            var list = new List<SuggestionRow>();
            void Add(string? path, string label, bool onlyWithScripts = false)
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
                var full = Path.GetFullPath(path).TrimEnd('\\');
                if (list.Any(s => s.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) return;
                int count = ScriptScanner.Count(full);
                if (onlyWithScripts && count == 0) return;
                list.Add(new SuggestionRow(full, label,
                    count >= 5000 ? "5000+ scripts" : count == 1 ? "1 script" : $"{count} scripts",
                    count > 0 ? "#DAFBE1" : "#EEF1F4", count > 0 ? "#1A7F37" : "#8C959F", count));
            }
            foreach (var r in recent) Add(r, "Recently opened");
            Add(@"C:\Ruchik\powershell-scripts", "Your PowerShell script repository");
            Add(Environment.CurrentDirectory, "Current folder", onlyWithScripts: true);
            Add(@"C:\Ruchik\BSTFS\bsone.deployment.qm\2_Packages\QM\Scripts", "QM deployment scripts");
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "scripts"), "Desktop scripts");
            return list;
        });

        Suggestions.ItemsSource = rows;
        var first = rows.FirstOrDefault(r => r.Count > 0);
        if (first is not null) Suggestions.SelectedItem = first;
        PathBox.Text = root ?? first?.Path ?? "";
        StatusText.Text = "Choose a folder to load its scripts.";
        PathBox.Focus();
        PathBox.SelectAll();
    }

    void OpenFolder(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0 || !Directory.Exists(path))
        {
            PickerError.Text = "Folder not found: " + path;
            PickerError.Visibility = Visibility.Visible;
            return;
        }
        var full = Path.GetFullPath(path).TrimEnd('\\');
        root = full;
        files = ScriptScanner.Scan(full, ScriptScanner.DefaultExclude);
        store.Recent = [full, .. store.Recent.Where(r => !r.Equals(full, StringComparison.OrdinalIgnoreCase))];
        SaveState();

        FolderName.Text = Path.GetFileName(full);
        FolderPath.Text = full;
        FolderPath.ToolTip = full;
        SearchBox.Text = "";
        BuildTree();
        ShowScript(null);
        PickerPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        StatusText.Text = $"Loaded {files.Count} scripts from {full}      Ctrl+F search  |  Ctrl+Enter run  |  Ctrl+O change folder";
        var last = files.FirstOrDefault(f => f.Full.Equals(store.Last, StringComparison.OrdinalIgnoreCase));
        if (last is not null) RevealEntry(last);
    }

    // ---- script tree ------------------------------------------------------------------------------

    void BuildTree()
    {
        var top = new TreeNode("");
        foreach (var f in files)
        {
            var node = top;
            node.Total++;
            var parts = f.Rel.Split('\\');
            foreach (var part in parts[..^1])
            {
                node = node.Child(part);
                node.Total++;
            }
            node.Scripts.Add(f);
        }
        Tree.Items.Clear();
        FillNode(Tree.Items, top);
        CountText.Text = files.Count == 1 ? "1 script" : $"{files.Count} scripts";
        EmptyText.Text = files.Count == 0
            ? "No .ps1 files in this folder. Use Change to pick another one."
            : "Pick a script on the left to read its comments, fill in its parameters and run it.";
    }

    /// <summary>Adds a folder's children; sub-folders fill in when first expanded.</summary>
    void FillNode(ItemCollection items, TreeNode node)
    {
        foreach (var sub in node.Folders)
        {
            // a folder holding just one script with the same name shows as that script
            if (sub.Folders.Count == 0 && sub.Scripts.Count == 1 && sub.Scripts[0].Name.Equals(sub.Name, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(NewScriptItem(sub.Scripts[0]));
                continue;
            }
            var item = new TreeViewItem { Header = NewHeader("\uE8B7", FolderIcon, sub.Name, sub.Total.ToString(), true), Tag = sub };
            item.Items.Add("...");
            items.Add(item);
        }
        foreach (var f in node.Scripts) items.Add(NewScriptItem(f));
    }

    static TreeViewItem NewScriptItem(ScriptEntry e) =>
        new() { Header = NewHeader("\uE943", ScriptIcon, e.Name, null, false), Tag = e, ToolTip = e.Rel };

    static StackPanel NewHeader(string glyph, Brush glyphBrush, string text, string? count, bool bold)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = glyphBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        });
        if (count is not null)
            panel.Children.Add(new TextBlock { Text = count, FontSize = 11, Foreground = Hint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        return panel;
    }

    void ExpandItem(TreeViewItem item)
    {
        if (item.Tag is TreeNode node && item.Items.Count == 1 && item.Items[0] is string)
        {
            item.Items.Clear();
            FillNode(item.Items, node);
        }
    }

    /// <summary>Expands the tree down to a script and selects it.</summary>
    void RevealEntry(ScriptEntry target)
    {
        var items = Tree.Items;
        foreach (var part in target.Rel.Split('\\'))
        {
            TreeViewItem? folder = null;
            foreach (var obj in items)
            {
                if (obj is not TreeViewItem tvi) continue;
                if (tvi.Tag is ScriptEntry e && e.Full == target.Full) { tvi.IsSelected = true; tvi.BringIntoView(); return; }
                if (tvi.Tag is TreeNode n && n.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) { folder = tvi; break; }
            }
            if (folder is null) return;
            ExpandItem(folder);
            folder.IsExpanded = true;
            items = folder.Items;
        }
    }

    void FillResults()
    {
        var terms = SearchBox.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            Results.Visibility = Visibility.Collapsed;
            Tree.Visibility = Visibility.Visible;
            CountText.Text = files.Count == 1 ? "1 script" : $"{files.Count} scripts";
            return;
        }
        // each result shows its own path, so no folder headings (most scripts sit in their own folder)
        var rows = new List<SearchRow>();
        int count = 0;
        foreach (var f in files)
        {
            if (!terms.All(t => f.Lower.Contains(t))) continue;
            if (++count > MaxResults) continue;
            rows.Add(new SearchRow(f.Name, f.Rel, "Collapsed", "Visible", true, f));
        }
        Results.ItemsSource = rows;
        Tree.Visibility = Visibility.Collapsed;
        Results.Visibility = Visibility.Visible;
        CountText.Text = count > MaxResults ? $"first {MaxResults} of {count} matches" : $"{count} of {files.Count} scripts";
    }

    // ---- details ----------------------------------------------------------------------------------

    ScriptInfo InfoFor(ScriptEntry e)
    {
        var stamp = File.GetLastWriteTimeUtc(e.Full);
        if (cache.TryGetValue(e.Full, out var i) && i.Stamp == stamp) return i;
        i = ScriptParser.Parse(e.Full);
        cache[e.Full] = i;
        return i;
    }

    Dictionary<string, object> ValuesFor(ScriptEntry e, ScriptInfo i)
    {
        if (valuesByScript.TryGetValue(e.Full, out var v)) return v;
        store.Values.TryGetValue(e.Full, out var saved);
        v = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["__extra"] = saved is not null && saved.TryGetValue("__extra", out var x) && x is string s ? s : "",
        };
        foreach (var p in i.Params)
        {
            object? old = null;
            saved?.TryGetValue(p.Name, out old);
            if (p.IsSwitch || p.IsBool) v[p.Name] = old is bool b ? b : Regex.IsMatch(p.Default, @"^\$true$", RegexOptions.IgnoreCase);
            else v[p.Name] = p.IsSecret ? "" : old as string ?? "";
        }
        valuesByScript[e.Full] = v;
        return v;
    }

    string EngineFor(ScriptInfo i) => i.Edition switch { "Desktop" => "powershell", "Core" => "pwsh", _ => engine };

    void ShowScript(ScriptEntry? e)
    {
        loading = true;
        entry = e;
        if (e is null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            info = null;
            vals = null;
            loading = false;
            return;
        }
        info = InfoFor(e);
        vals = ValuesFor(e, info);
        store.Last = e.Full;
        EmptyState.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        ScriptTitle.Text = e.Name;
        ScriptPath.Text = e.Rel;
        ScriptPath.ToolTip = e.Full;

        Chips.Children.Clear();
        Chips.Children.Add(Chip(e.Folder));
        int n = info.Params.Count;
        Chips.Children.Add(Chip(n == 0 ? "No parameters" : n == 1 ? "1 parameter" : $"{n} parameters"));
        int required = info.Params.Count(p => p.Mandatory);
        if (required > 0) Chips.Children.Add(Chip($"{required} required", "#FFF8C5", "#9A6700"));
        if (info.Edition == "Core") Chips.Children.Add(Chip("PowerShell 7 only", "#DDF4FF", "#0969DA"));
        if (info.Edition == "Desktop") Chips.Children.Add(Chip("Windows PowerShell only", "#DDF4FF", "#0969DA"));

        fullHelp = false;
        FillHelp();
        ExtraArgs.Text = vals["__extra"] as string ?? "";
        BuildParams();
        loading = false;
        UpdatePreview();
    }

    static Border Chip(string text, string back = "#EEF1F4", string fore = "#57606A") => new()
    {
        CornerRadius = new CornerRadius(10),
        Background = Freeze(back),
        Padding = new Thickness(9, 2, 9, 2),
        Margin = new Thickness(0, 0, 6, 4),
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = Freeze(fore) },
    };

    void FillHelp()
    {
        HelpPanel.Children.Clear();
        var i = info!;
        void Section(string title, string body, bool mono = false)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            HelpPanel.Children.Add(new TextBlock
            {
                Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Purple,
                Margin = new Thickness(0, HelpPanel.Children.Count > 0 ? 12 : 0, 0, 4),
            });
            var t = new TextBlock { Text = body.Trim(), TextWrapping = TextWrapping.Wrap, Foreground = Body };
            if (mono) { t.FontFamily = new FontFamily("Consolas"); t.FontSize = 12.5; }
            HelpPanel.Children.Add(t);
        }
        Section("SYNOPSIS", i.Synopsis);
        Section("DESCRIPTION", i.Description);
        Section("NOTES", i.Notes);
        if (i.Comments.Count > 0) Section("COMMENTS", string.Join("\n", i.Comments));
        if (fullHelp)
        {
            if (i.Params.Count > 0)
            {
                var docs = i.Params.Select(p =>
                {
                    var flags = new List<string> { p.TypeName };
                    if (p.Mandatory) flags.Add("required");
                    if (p.Default.Length > 0) flags.Add("default " + p.Default);
                    return $"-{p.Name}  ({string.Join(", ", flags)})" + (p.Description.Length > 0 ? "\n    " + p.Description : "");
                });
                Section("PARAMETERS", string.Join("\n", docs));
            }
            if (i.Examples.Count > 0) Section("EXAMPLES", string.Join("\n\n", i.Examples), mono: true);
            if (i.Links.Count > 0) Section("LINKS", string.Join("\n", i.Links));
        }
        if (HelpPanel.Children.Count == 0)
            HelpPanel.Children.Add(new TextBlock { Text = "This script has no comment-based help or header comments.", Foreground = Muted });
        bool more = i.Examples.Count > 0 || i.Params.Count > 0 || i.Links.Count > 0;
        FullHelpBtn.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        FullHelpBtn.Content = fullHelp ? "Show less" : "Show full help";
    }

    void UpdatePreview()
    {
        if (loading || entry is null || info is null || vals is null) return;
        CommandPreview.Text = CommandBuilder.Build(entry, info, vals).Display;
    }

    void BuildParams()
    {
        ParamPanel.Children.Clear();
        inputs.Clear();
        var i = info!;
        var v = vals!;
        RequiredNote.Visibility = i.Params.Any(p => p.Mandatory) ? Visibility.Visible : Visibility.Collapsed;
        if (i.Params.Count == 0)
        {
            ParamPanel.Children.Add(new TextBlock { Text = "This script has no parameters - just press Run.", Foreground = Muted });
            return;
        }
        var inputStyle = (Style)FindResource("Input");
        var pwdStyle = (Style)FindResource("Pwd");
        foreach (var p in i.Params)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var label = new TextBlock { Margin = new Thickness(0, 0, 0, 6) };
            label.Inlines.Add(new Run(p.Name) { FontWeight = FontWeights.SemiBold });
            if (p.Mandatory) label.Inlines.Add(new Run(" *") { Foreground = Red, FontWeight = FontWeights.SemiBold });
            label.Inlines.Add(new Run("    " + p.TypeName) { Foreground = Hint, FontSize = 11.5 });
            block.Children.Add(label);
            var name = p.Name;
            FrameworkElement control;

            if (p.IsSwitch || p.IsBool)
            {
                var cb = new CheckBox { Content = p.IsSwitch ? $"Use -{p.Name}" : "True", IsChecked = v[name] is true };
                cb.Checked += (_, _) => { v[name] = true; UpdatePreview(); };
                cb.Unchecked += (_, _) => { v[name] = false; UpdatePreview(); };
                control = cb;
                block.Children.Add(cb);
            }
            else if (p.ValidSet.Count > 0)
            {
                var combo = new ComboBox { Width = 460, HorizontalAlignment = HorizontalAlignment.Left };
                combo.Items.Add("");
                foreach (var option in p.ValidSet) combo.Items.Add(option);
                if (v[name] is string { Length: > 0 } current) combo.SelectedItem = current;
                combo.SelectionChanged += (_, _) =>
                {
                    v[name] = combo.SelectedItem as string ?? "";
                    combo.ClearValue(Control.BorderBrushProperty);
                    UpdatePreview();
                };
                control = combo;
                block.Children.Add(combo);
            }
            else if (p.IsCredential)
            {
                control = new TextBlock { Text = "The script asks for these credentials itself - use \"Console window\".", Foreground = Muted };
                block.Children.Add(control);
            }
            else if (p.IsSecret)
            {
                var pwd = new PasswordBox { Style = pwdStyle, Width = 460, HorizontalAlignment = HorizontalAlignment.Left, Password = v[name] as string ?? "" };
                pwd.PasswordChanged += (_, _) =>
                {
                    v[name] = pwd.Password;
                    pwd.ClearValue(Control.BorderBrushProperty);
                    UpdatePreview();
                };
                control = pwd;
                block.Children.Add(pwd);
            }
            else
            {
                var host = new Grid { Width = 460, HorizontalAlignment = HorizontalAlignment.Left };
                var box = new TextBox { Style = inputStyle, Text = v[name] as string ?? "" };
                host.Children.Add(box);
                var hintText = p.Default.Length > 0 ? "default: " + p.Default : p.IsArray ? "value1, value2, ..." : "";
                TextBlock? hint = null;
                if (hintText.Length > 0)
                {
                    hint = new TextBlock
                    {
                        Text = hintText, Foreground = Hint, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false, Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible,
                    };
                    host.Children.Add(hint);
                }
                box.TextChanged += (_, _) =>
                {
                    v[name] = box.Text;
                    if (hint is not null) hint.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                    box.ClearValue(Control.BorderBrushProperty);
                    UpdatePreview();
                };
                control = box;
                block.Children.Add(host);
            }

            var notes = new List<string>();
            if (p.Description.Length > 0) notes.Add(p.Description);
            if (p.IsArray && p.Default.Length > 0) notes.Add("Separate multiple values with commas.");
            if (notes.Count > 0)
                block.Children.Add(new TextBlock
                {
                    Text = string.Join(" ", notes), TextWrapping = TextWrapping.Wrap, MaxWidth = 680,
                    HorizontalAlignment = HorizontalAlignment.Left, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 5, 0, 0),
                });
            ParamPanel.Children.Add(block);
            inputs.Add((p, control));
        }
    }

    // ---- output panel -------------------------------------------------------------------------------

    void AppendLines(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            Brush? brush = null;
            var text = raw;
            if (raw.Length >= 3 && raw[0] == '~' && raw[2] == '~')
            {
                brush = raw[1] switch { 'E' => OutErr, 'W' => OutWarn, 'V' => OutVerbose, 'R' => OutInfo, 'S' => OutOk, _ => null };
                text = raw[3..];
            }
            var run = new Run(text);
            if (brush is not null) run.Foreground = brush;
            para.Inlines.Add(run);
            para.Inlines.Add(new LineBreak());
            lineCount++;
        }
        if (lineCount > 8000)
        {
            for (int k = 0; k < 4000 && para.Inlines.FirstInline is { } first; k++) para.Inlines.Remove(first);
            lineCount -= 2000;
        }
        OutputBox.ScrollToEnd();
    }

    void UpdatePill()
    {
        var r = viewing;
        string key = "Idle", label = "Idle";
        if (r is not null)
        {
            key = label = r.Status;
            if (r.Status == "Running" && !r.Captured) { key = "Console"; label = "In console"; }
        }
        var (bg, fg) = Pills[key];
        OutputPill.Background = Freeze(bg);
        OutputStatus.Foreground = Freeze(fg);
        OutputStatus.Text = label;
        StopBtn.IsEnabled = r is not null && !r.Done;
        CopyOutBtn.IsEnabled = r is not null && r.Lines.Count > 0;
    }

    void ShowRun(RunSession? r)
    {
        viewing = r;
        para.Inlines.Clear();
        lineCount = 0;
        if (r is null) OutputTitle.Text = "Run a script to see its output here.";
        else
        {
            OutputTitle.Text = $"{r.Script}   -   started {r.Started}";
            if (r.Captured) AppendLines(r.Lines);
            else AppendLines(["~R~" + r.Command, "", "~R~This run opened in its own console window - its output is shown there."]);
        }
        UpdatePill();
    }

    /// <summary>Moves output from background threads into the panel and finishes runs that ended.</summary>
    void OnTick()
    {
        tick++;
        foreach (var r in runs)
        {
            if (r.Done) continue;
            if (!r.Pending.IsEmpty)
            {
                var batch = new List<string>();
                while (r.Pending.TryDequeue(out var line)) batch.Add(line);
                r.Lines.AddRange(batch);
                if (viewing == r) AppendLines(batch);
            }
            if (r.Exited && r.Pending.IsEmpty) FinishRun(r);
            else if (tick % 4 == 0) r.Duration = r.Watch.Elapsed.ToString(@"hh\:mm\:ss");
        }
    }

    void FinishRun(RunSession r)
    {
        r.Watch.Stop();
        r.Done = true;
        var elapsed = r.Watch.Elapsed.ToString(@"hh\:mm\:ss");
        r.Exit = r.ExitCode.ToString();
        r.Duration = elapsed;
        string final;
        if (r.Stopped) { r.Status = "Stopped"; r.StatusColor = "#6E7781"; final = "~E~Stopped after " + elapsed; }
        else if (r.ExitCode == 0) { r.Status = "Succeeded"; r.StatusColor = "#1A7F37"; final = "~S~Finished successfully in " + elapsed; }
        else { r.Status = "Failed"; r.StatusColor = "#CF222E"; final = $"~E~Finished with exit code {r.ExitCode} after {elapsed}"; }
        if (r.Captured)
        {
            r.Lines.AddRange(["", final]);
            if (viewing == r) AppendLines(["", final]);
        }
        store.Log($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | exit {r.ExitCode} | {elapsed} | {r.Engine} | {r.FullCommand}");
        if (viewing == r) UpdatePill();
        StatusText.Text = $"{r.Script}: {r.Status.ToLowerInvariant()} (exit code {r.ExitCode}, {elapsed})";
        r.Process?.Dispose();
    }

    // ---- run ----------------------------------------------------------------------------------------

    void RunSelected()
    {
        if (entry is null || info is null || vals is null) return;
        var run = CommandBuilder.Build(entry, info, vals);
        if (run.Missing.Count > 0)
        {
            foreach (var (p, control) in inputs)
                if (run.Missing.Contains(p.Name) && control is Control c) c.BorderBrush = Red;
            MessageBox.Show(this, "Please fill in the required parameter(s):\n\n  -" + string.Join("\n  -", run.Missing),
                "Missing values", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var eng = EngineFor(info);
        var exe = Engines.Resolve(eng);
        if (exe is null)
        {
            MessageBox.Show(this, Engines.Label(eng) + " is not installed.", "Cannot run", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        SaveState();

        bool captured = ModeApp.IsChecked == true;
        var session = new RunSession
        {
            Script = entry.Name,
            Command = run.Display,
            FullCommand = run.Display.Replace("& '.\\", "& '" + root + "\\"),
            Engine = eng,
            Captured = captured,
        };
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = entry.Dir };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        if (captured) psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(CommandBuilder.Encode(CommandBuilder.ChildScript(entry, run, capture: captured, pauseAtEnd: !captured)));
        if (captured)
        {
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
        }

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (captured)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) session.Pending.Enqueue(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) session.Pending.Enqueue("~E~" + e.Data); };
            }
            process.Exited += (_, _) =>
            {
                try { process.WaitForExit(); session.ExitCode = process.ExitCode; } catch { session.ExitCode = -1; }
                session.Exited = true;
            };
            if (captured)
            {
                session.Lines.AddRange([$"~R~> {run.Display}", $"~R~Started {session.Started} with {Engines.Label(eng)} in {entry.Dir}", ""]);
            }
            process.Start();
            if (captured)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            session.Process = process;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        runs.Insert(0, session);
        BottomTabs.SelectedIndex = 0;
        ShowRun(session);
        StatusText.Text = captured ? $"Running {entry.Name} with {Engines.Label(eng)}..." : $"Opened {entry.Name} in a console window.";
    }

    void SaveState()
    {
        foreach (var (full, v) in valuesByScript)
        {
            cache.TryGetValue(full, out var i);
            var keep = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, val) in v)
            {
                var p = i?.Params.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (p is not null && p.IsSecret) continue;   // secrets are never written to disk
                if (val is bool || val is string { Length: > 0 }) keep[name] = val;
            }
            store.Values[full] = keep;
        }
        store.Engine = engine;
        store.Save();
    }

    // ---- events -------------------------------------------------------------------------------------

    void WireEvents()
    {
        Tree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TreeViewItem item) ExpandItem(item);
        }));
        Tree.SelectedItemChanged += (_, _) =>
        {
            if (Tree.SelectedItem is TreeViewItem { Tag: ScriptEntry e } && entry?.Full != e.Full) ShowScript(e);
        };
        Tree.MouseDoubleClick += (_, _) => { if (Tree.SelectedItem is TreeViewItem { Tag: ScriptEntry }) RunSelected(); };
        Results.SelectionChanged += (_, _) =>
        {
            if (Results.SelectedItem is SearchRow { Entry: { } e } && entry?.Full != e.Full) ShowScript(e);
        };
        Results.MouseDoubleClick += (_, _) => { if (entry is not null) RunSelected(); };
        SearchBox.TextChanged += (_, _) =>
        {
            SearchHint.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            FillResults();
        };
        SearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { SearchBox.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Down && Results.Visibility == Visibility.Visible && Results.ItemsSource is List<SearchRow> rows)
            {
                var first = rows.FirstOrDefault(r => r.Entry is not null);
                if (first is not null)
                {
                    Results.SelectedItem = first;
                    Results.UpdateLayout();
                    (Results.ItemContainerGenerator.ContainerFromItem(first) as UIElement)?.Focus();
                }
                e.Handled = true;
            }
        };
        CollapseBtn.Click += (_, _) => { foreach (var o in Tree.Items) if (o is TreeViewItem t) t.IsExpanded = false; };
        RescanBtn.Click += (_, _) => { cache.Clear(); if (root is not null) OpenFolder(root); };
        ChangeFolderBtn.Click += async (_, _) => await ShowPickerAsync();
        FullHelpBtn.Click += (_, _) => { fullHelp = !fullHelp; FillHelp(); };
        ExtraArgs.TextChanged += (_, _) =>
        {
            if (vals is null || loading) return;
            vals["__extra"] = ExtraArgs.Text;
            UpdatePreview();
        };
        EngineBox.SelectionChanged += (_, _) =>
        {
            if (EngineBox.SelectedItem is ComboBoxItem { Tag: string name } && name != engine)
            {
                engine = name;
                if (entry is not null) ShowScript(entry);
            }
        };
        RunBtn.Click += (_, _) => RunSelected();
        CopyCmdBtn.Click += (_, _) =>
        {
            if (CommandPreview.Text.Length == 0) return;
            Clipboard.SetText(CommandPreview.Text);
            StatusText.Text = "Command copied (secrets masked).";
        };
        EditBtn.Click += (_, _) =>
        {
            if (entry is null) return;
            try { Process.Start(new ProcessStartInfo("code", $"\"{entry.Full}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); }
            catch { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{entry.Full}\"")); }
        };
        FolderBtn.Click += (_, _) => { if (entry is not null) Process.Start("explorer.exe", $"/select,\"{entry.Full}\""); };
        StopBtn.Click += (_, _) =>
        {
            if (viewing is not { Done: false, Process: { } p } r) return;
            r.Stopped = true;
            try { p.Kill(entireProcessTree: true); } catch { }
        };
        CopyOutBtn.Click += (_, _) =>
        {
            if (viewing is null) return;
            Clipboard.SetText(string.Join("\r\n", viewing.Lines.Select(l => l.Length >= 3 && l[0] == '~' && l[2] == '~' ? l[3..] : l)));
            StatusText.Text = "Output copied.";
        };
        ClearOutBtn.Click += (_, _) => ShowRun(null);
        HistoryList.MouseDoubleClick += (_, _) =>
        {
            if (HistoryList.SelectedItem is RunSession r) { BottomTabs.SelectedIndex = 0; ShowRun(r); }
        };

        // folder page
        Suggestions.SelectionChanged += (_, _) => { if (Suggestions.SelectedItem is SuggestionRow s) PathBox.Text = s.Path; };
        Suggestions.MouseDoubleClick += (_, _) => { if (Suggestions.SelectedItem is SuggestionRow s) OpenFolder(s.Path); };
        OpenBtn.Click += (_, _) => OpenFolder(PathBox.Text);
        PathBox.KeyDown += (_, e) => { if (e.Key == Key.Return) OpenFolder(PathBox.Text); };
        BrowseBtn.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose the folder that contains your scripts" };
            if (Directory.Exists(PathBox.Text)) dialog.InitialDirectory = PathBox.Text;
            if (dialog.ShowDialog(this) == true) { PathBox.Text = dialog.FolderName; OpenFolder(dialog.FolderName); }
        };
        PickerBackBtn.Click += (_, _) => { PickerPage.Visibility = Visibility.Collapsed; MainPage.Visibility = Visibility.Visible; };

        PreviewKeyDown += async (_, e) =>
        {
            if (MainPage.Visibility != Visibility.Visible || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (e.Key == Key.Return) { RunSelected(); e.Handled = true; }
            else if (e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.O) { e.Handled = true; await ShowPickerAsync(); }
        };
        Closing += (_, e) =>
        {
            var active = runs.Where(r => !r.Done && r.Captured).ToList();
            if (active.Count > 0)
            {
                var answer = MessageBox.Show(this, $"{active.Count} script(s) are still running in the output panel. Stop them and close?",
                    "Script Console", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
                foreach (var r in active) { try { r.Process?.Kill(entireProcessTree: true); } catch { } }
            }
            timer.Stop();
            SaveState();
        };
    }
}
