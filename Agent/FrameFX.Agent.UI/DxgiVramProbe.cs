using Vortice.DXGI;
using UniversalFrameFX.Agent;

namespace UniversalFrameFX.Agent.UI;

/// <summary>Largest usable VRAM among hardware adapters (skips software adapters). iGPUs also count part of their shared memory.</summary>
public static class DxgiVramProbe
{
    public static (long Bytes, string Name) Best()
    {
        long best = 0; string name = "unknown GPU";
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            using (adapter)
            {
                var d = adapter.Description1;
                if ((d.Flags & AdapterFlags.Software) != 0) continue;
                long mem = ContextSizer.EffectiveVram((long)d.DedicatedVideoMemory, (long)d.SharedSystemMemory);
                if (mem > best) { best = mem; name = d.Description; }
            }
        }
        return (best, name);
    }
}
