namespace UniversalFrameFX.Agent;

/// <summary>Persists Yes/No for running LarkquillAI (cloud panel and local agent) under the agent root.</summary>
public static class LarkquillPermissionStore
{
    public const string FileName = "larkquill-local-permission.json";

    public sealed class State
    {
        /// <summary>null = unset (ask), true = allowed, false = unavailable.</summary>
        public bool? Allowed { get; set; }
    }

    public static string PathFor(string root) => Path.Combine(root, FileName);

    public static State Load(string root)
    {
        try
        {
            string f = PathFor(root);
            if (!File.Exists(f)) return new();
            return System.Text.Json.JsonSerializer.Deserialize<State>(File.ReadAllText(f)) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(string root, State state)
    {
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(PathFor(root), System.Text.Json.JsonSerializer.Serialize(state));
        }
        catch { }
    }

    public static void Clear(string root)
    {
        try
        {
            string f = PathFor(root);
            if (File.Exists(f)) File.Delete(f);
        }
        catch { }
    }
}
