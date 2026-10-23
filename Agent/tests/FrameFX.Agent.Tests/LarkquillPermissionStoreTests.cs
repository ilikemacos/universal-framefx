using Xunit;
using UniversalFrameFX.Agent;

namespace FrameFX.Agent.Tests;

public class LarkquillPermissionStoreTests
{
    [Fact]
    public void Load_missing_file_returns_unset()
    {
        string root = Path.Combine(Path.GetTempPath(), "framefx-perm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = LarkquillPermissionStore.Load(root);
            Assert.Null(state.Allowed);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Save_and_Load_roundtrip_yes_and_no()
    {
        string root = Path.Combine(Path.GetTempPath(), "framefx-perm-" + Guid.NewGuid().ToString("N"));
        try
        {
            LarkquillPermissionStore.Save(root, new LarkquillPermissionStore.State { Allowed = true });
            Assert.True(LarkquillPermissionStore.Load(root).Allowed);

            LarkquillPermissionStore.Save(root, new LarkquillPermissionStore.State { Allowed = false });
            Assert.False(LarkquillPermissionStore.Load(root).Allowed);

            LarkquillPermissionStore.Clear(root);
            Assert.Null(LarkquillPermissionStore.Load(root).Allowed);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void PathFor_uses_expected_filename()
    {
        Assert.EndsWith(LarkquillPermissionStore.FileName, LarkquillPermissionStore.PathFor("/tmp/agent"));
    }

    static void TryDelete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
