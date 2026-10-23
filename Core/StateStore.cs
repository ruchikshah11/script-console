namespace ScriptConsole.Core;

/// <summary>
/// Remembered engine, last script, recent folders and per-script parameter values.
/// Same file and shape as the PowerShell Script Console, so both share what you typed.
/// </summary>
public sealed class StateStore
{
    public string DataDir { get; }
    public string StateFile => Path.Combine(DataDir, "state.json");
    public string RunLogFile => Path.Combine(DataDir, "runs.log");

    public string? Engine { get; set; }
    public string? Last { get; set; }
    public List<string> Recent { get; set; } = [];
    public Dictionary<string, Dictionary<string, object>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    StateStore(string dataDir) => DataDir = dataDir;

    public static StateStore Load()
    {
        var dir = Environment.GetEnvironmentVariable("SCRIPTCONSOLE_DATA");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScriptConsole");
        var store = new StateStore(dir);
        try
        {
            if (!File.Exists(store.StateFile)) return store;
            if (JsonNode.Parse(File.ReadAllText(store.StateFile)) is not JsonObject root) return store;
            store.Engine = StringOf(root["engine"]);
            store.Last = StringOf(root["last"]);
            if (root["recent"] is JsonArray recent) store.Recent = recent.Select(StringOf).OfType<string>().ToList();
            if (root["values"] is JsonObject scripts)
            {
                foreach (var (path, node) in scripts)
                {
                    if (node is not JsonObject obj) continue;
                    var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, value) in obj)
                    {
                        if (value is not JsonValue v) continue;
                        if (v.TryGetValue<bool>(out var b)) values[name] = b;
                        else if (v.TryGetValue<string>(out var s)) values[name] = s;
                        else values[name] = v.ToJsonString();
                    }
                    store.Values[path] = values;
                }
            }
        }
        catch { /* a damaged state file just starts fresh */ }
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var scripts = new JsonObject();
            foreach (var (path, values) in Values)
            {
                var obj = new JsonObject();
                foreach (var (name, value) in values)
                    obj[name] = value is bool b ? JsonValue.Create(b) : JsonValue.Create(value.ToString());
                scripts[path] = obj;
            }
            var root = new JsonObject
            {
                ["engine"] = Engine,
                ["last"] = Last,
                ["recent"] = new JsonArray(Recent.Take(8).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["values"] = scripts,
            };
            File.WriteAllText(StateFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* saving is best effort */ }
    }

    public void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(RunLogFile, line + Environment.NewLine);
        }
        catch { }
    }

    static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
