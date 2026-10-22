using System.Security.Cryptography;
using System.Text;
using UniversalFrameFX.Agent.Cloud;

namespace UniversalFrameFX.Agent.UI;

/// <summary>
/// Keeps the LarkquillAI API key encrypted with Windows DPAPI (current user only) in
/// %LOCALAPPDATA%\FrameFX\larkquill.key. Other Windows users and other PCs can't decrypt it.
/// </summary>
public sealed class DpapiKeyStore(string? file = null) : ILarkquillKeyStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Universal-FrameFX/LarkquillAI/v1");
    public string File { get; } = file ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameFX", "larkquill.key");

    public string? Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            var plain = ProtectedData.Unprotect(System.IO.File.ReadAllBytes(File), Entropy, DataProtectionScope.CurrentUser);
            string k = Encoding.UTF8.GetString(plain).Trim();
            return k.Length > 0 ? k : null;
        }
        catch (CryptographicException) { return null; } // copied from another user/PC: ask again
        catch (IOException) { return null; }
    }

    public void Save(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        string tmp = File + ".tmp";
        System.IO.File.WriteAllBytes(tmp, enc);
        System.IO.File.Move(tmp, File, overwrite: true);
    }

    public void Clear() { try { System.IO.File.Delete(File); } catch (IOException) { } }

    /// <summary>"lq_…abcd" style hint for the UI; never shows the whole key.</summary>
    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "" : key.Length <= 8 ? new string('•', key.Length) : key[..4] + "…" + key[^4..];
}
