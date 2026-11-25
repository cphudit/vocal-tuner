using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VocalTuner.Coach;

/// <summary>Every analysis is saved as JSON in %LOCALAPPDATA%\VocalTuner\coach so progress can be tracked.</summary>
public static class History
{
    public static string Dir => Path.Combine(AudioEngine.DataDir, "coach");

    static readonly JsonSerializerOptions Opt = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string ToJson(CoachResult r, bool indented = false) =>
        JsonSerializer.Serialize(r, indented ? new JsonSerializerOptions(Opt) { WriteIndented = true } : Opt);

    public static CoachResult? FromFile(string path)
    {
        try { return JsonSerializer.Deserialize<CoachResult>(File.ReadAllText(path), Opt); }
        catch { return null; }
    }

    public static void Save(CoachResult r)
    {
        Directory.CreateDirectory(Dir);
        string id = r.When.ToString("yyyyMMdd-HHmmss");
        while (File.Exists(Path.Combine(Dir, id + ".json"))) id += "b";
        r.Id = id;
        File.WriteAllText(Path.Combine(Dir, id + ".json"), ToJson(r));
    }

    public static void Delete(string id)
    {
        string p = Path.Combine(Dir, id + ".json");
        if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>All saved analyses, oldest first.</summary>
    public static List<CoachResult> LoadAll()
    {
        if (!Directory.Exists(Dir)) return new();
        return Directory.GetFiles(Dir, "*.json")
            .Select(FromFile).Where(r => r != null).Select(r => r!)
            .OrderBy(r => r.When).ToList();
    }

    /// <summary>The take to compare with: the latest earlier analysis of the same source, else the latest earlier one.</summary>
    public static CoachResult? Previous(CoachResult r, IEnumerable<CoachResult> all)
    {
        var earlier = all.Where(o => o.When < r.When && o.Id != r.Id).OrderByDescending(o => o.When).ToList();
        return earlier.FirstOrDefault(o => o.Source == r.Source && r.Kind == "file") ?? earlier.FirstOrDefault();
    }
}
