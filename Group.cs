// Group mode shared by System HUD and AI Quota HUD (same file in both repos). The two widgets talk through one small file,
// %APPDATA%\HudGroup\group.txt: "link" (move and resize together), "same" (same colours and font) and the shared values.
// Each widget writes it when its own settings change and reads it once a second; a change made by the other is applied once.
// Writes go to a temp file that then replaces group.txt, so the other process never reads half a file; a failed write or
// read is retried on the next change / poll instead of being remembered as done.
namespace HudWidget;

static class Group
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HudGroup", "group.txt");
    static DateTime seen;
    static string lastContent = "";

    static string Body(IEnumerable<KeyValuePair<string, string>> kv) => string.Join("\n", kv.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

    public static void Write(string from, IDictionary<string, string> kv)
    {
        var body = Body(kv);
        if (body == lastContent) return;                      // nothing new to tell the other widget
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var tmp = FilePath + "." + from + ".tmp";
            File.WriteAllText(tmp, $"from={from}\n{body}\n");
            File.Move(tmp, FilePath, true);
            lastContent = body; seen = File.GetLastWriteTimeUtc(FilePath);
        }
        catch { }
    }

    // values written by the OTHER widget since the last call, or null
    public static Dictionary<string, string> ReadNew(string me)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var t = File.GetLastWriteTimeUtc(FilePath);
            if (t == seen) return null;
            var kv = File.ReadAllLines(FilePath).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (!kv.TryGetValue("from", out var from) || !kv.ContainsKey("link")) return null;   // incomplete: look again next poll
            seen = t;
            if (from == me) return null;
            kv.Remove("from");
            lastContent = Body(kv);
            return kv;
        }
        catch { return null; }
    }
}
