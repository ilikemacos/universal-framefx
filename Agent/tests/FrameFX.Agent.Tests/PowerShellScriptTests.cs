using Xunit;

namespace FrameFX.Agent.Tests;

/// <summary>
/// Windows PowerShell 5.1 reads a BOM-less .ps1 as the ANSI code page, so any non-ASCII character
/// (an arrow, an ellipsis) turns into mojibake and can break parsing. Keep every .ps1 in the repo ASCII-only,
/// and give the setup script a UTF-8 BOM. The irm | iex installers (install.ps1) must NOT have a BOM:
/// Invoke-RestMethod would turn it into stray characters in front of the script.
/// </summary>
public class PowerShellScriptTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git")) && !File.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        Assert.True(d != null, "repo root (.git) not found above " + AppContext.BaseDirectory);
        return d!.FullName;
    }

    static IEnumerable<string> Scripts(string root) =>
        Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(p => p is ".git" or "bin" or "obj" or "node_modules"));

    [Fact]
    public void AllPs1FilesAreAscii()
    {
        var root = RepoRoot();
        var files = Scripts(root).ToList();
        Assert.NotEmpty(files);
        var bad = new List<string>();
        foreach (var f in files)
        {
            var b = File.ReadAllBytes(f);
            int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            int line = 1;
            for (int i = start; i < b.Length; i++)
            {
                if (b[i] == (byte)'\n') line++;
                else if (b[i] > 0x7F) { bad.Add($"{Path.GetRelativePath(root, f)}:{line}"); break; }
            }
        }
        Assert.True(bad.Count == 0, "Non-ASCII characters in .ps1 files: " + string.Join(", ", bad));
    }

    [Fact]
    public void SetupScriptHasBomAndInstallersDoNot()
    {
        var root = RepoRoot();
        foreach (var f in Scripts(root))
        {
            var b = File.ReadAllBytes(f);
            bool bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            string name = Path.GetFileName(f);
            if (name == "Setup-FrameFXAgent.ps1") Assert.True(bom, $"{f} needs a UTF-8 BOM");
            if (name == "install.ps1") Assert.False(bom, $"{f} is run through irm | iex and must not have a BOM");
        }
    }
}
