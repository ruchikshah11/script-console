using System.Windows;

namespace ScriptConsole;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Options options;
        try { options = Options.Parse(args); }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message + "\n\n" + Options.Usage, "Script Console", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 2;
        }
        if (options.Help)
        {
            MessageBox.Show(Options.Usage, "Script Console", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        if (options.Dump is not null) return DumpParsed(options.Dump);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ScriptConsole;component/Themes/Styles.xaml"),
        });
        app.DispatcherUnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            MessageBox.Show("Something went wrong: " + e.Exception.Message, "Script Console", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        return app.Run(new MainWindow(options));
    }

    /// <summary>Appends unexpected errors to %APPDATA%\ScriptConsole\errors.log.</summary>
    static void LogCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScriptConsole");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>--dump: what the parser reads from every script under a folder, one JSON line each (written to a file next to stdout-less WinExe).</summary>
    static int DumpParsed(string root)
    {
        var sb = new StringBuilder();
        foreach (var e in ScriptScanner.Scan(root, ScriptScanner.DefaultExclude))
        {
            try
            {
                var i = ScriptParser.Parse(e.Full);
                sb.AppendLine(JsonSerializer.Serialize(new
                {
                    e.Rel, i.Synopsis, i.Edition,
                    Params = i.Params.Select(x => new { x.Name, x.TypeName, x.Mandatory, x.IsSwitch, x.IsSecure, x.IsArray, Set = x.ValidSet.Count, x.Default }),
                }));
            }
            catch (Exception ex) { sb.AppendLine(JsonSerializer.Serialize(new { e.Rel, Error = ex.Message })); }
        }
        var output = Environment.GetEnvironmentVariable("SCRIPTCONSOLE_DUMP") ?? Path.Combine(Path.GetTempPath(), "ScriptConsole-dump.jsonl");
        File.WriteAllText(output, sb.ToString());
        return 0;
    }
}
