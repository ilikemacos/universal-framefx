namespace UniversalFrameFX.PresetAi;

public static class PresetModelSettings
{
    public static PresetModelKind DefaultKind
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("UFX_PRESET_MODEL");
            return string.Equals(env, "g14", StringComparison.OrdinalIgnoreCase) ? PresetModelKind.G14 : PresetModelKind.HybridR3;
        }
    }

    public static OnnxPresetModel? LoadDefault() => OnnxPresetModel.TryLoad(DefaultKind) ?? OnnxPresetModel.TryLoad(PresetModelKind.HybridR3);
}
