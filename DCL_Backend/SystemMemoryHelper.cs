using System.Runtime.InteropServices;

namespace DCL_Launcher;

internal static class SystemMemoryHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static (int TotalMb, int AvailableMb) GetSystemMemoryMb()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            int total = (int)Math.Max(2048, status.ullTotalPhys / (1024UL * 1024UL));
            int avail = (int)Math.Max(1024, status.ullAvailPhys / (1024UL * 1024UL));
            return (total, avail);
        }
        return (16384, 8192);
    }

    /// <summary>
    /// Computes recommended -Xmx in 512 MB steps based on physical total/free RAM,
    /// loader overhead, MC version generation, mod count, shaderpacks, modpack heuristics,
    /// and active GC strategy (G1GC vs ZGC/Generational ZGC pause thresholds).
    /// </summary>
    public static int ComputeRecommendedMemoryMb(
        int totalRamMb,
        int availRamMb,
        string? mcVersion,
        string? loader,
        int modCount,
        bool isModpack,
        int shaderCount = 0,
        bool usesZgc = false)
    {
        int mcMinor = ParseMcMinor(mcVersion);
        bool isModern = mcMinor >= 18;
        bool isMid = mcMinor >= 13 && mcMinor < 18;
        bool isVanilla = string.IsNullOrWhiteSpace(loader) || loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase);
        bool isHeavyLoader = !isVanilla && (
            loader!.Equals("Forge", StringComparison.OrdinalIgnoreCase) ||
            loader.Equals("NeoForge", StringComparison.OrdinalIgnoreCase));

        int safeMods = Math.Max(0, modCount);
        int baseMb;
        if (isVanilla)
            baseMb = isModern ? 2560 : isMid ? 2048 : 1536;
        else if (isHeavyLoader)
            baseMb = isModern ? 3584 : isMid ? 3072 : 2560;
        else
            baseMb = isModern ? 3072 : isMid ? 2560 : 2048;

        // Progressive mod memory curve: diminishing per-mod overhead on huge packs prevents over-allocation
        int modExtraMb = 0;
        if (safeMods > 0)
        {
            double firstBand = Math.Min(safeMods, 80) * (isHeavyLoader ? 28.0 : 22.0);
            double midBand = Math.Clamp(safeMods - 80, 0, 140) * (isHeavyLoader ? 22.0 : 17.0);
            double heavyBand = Math.Max(0, safeMods - 220) * (isHeavyLoader ? 16.0 : 12.0);
            modExtraMb = Math.Min(7168, (int)Math.Round(firstBand + midBand + heavyBand));
        }

        int shaderExtraMb = shaderCount > 0 ? (safeMods >= 50 || isModpack ? 1024 : 512) : 0;
        int desiredMb = baseMb + modExtraMb + shaderExtraMb;

        if (isModpack)
        {
            int packFloor = safeMods >= 240 ? 8192 : safeMods >= 120 ? 6144 : 5120;
            desiredMb = Math.Max(desiredMb + 512, packFloor);
        }
        else if (safeMods >= 150)
        {
            desiredMb = Math.Max(desiredMb, 6144);
        }

        // Leave safe headroom for Windows, GPU drivers, and WebView2 without starving smaller machines
        int osReserveMb = totalRamMb >= 32768 ? 4096 : totalRamMb >= 16384 ? 3072 : totalRamMb >= 8192 ? 2048 : 1536;
        double maxShare = totalRamMb >= 32768 ? 0.70 : totalRamMb >= 16384 ? 0.65 : 0.60;
        int freeBasedCap = Math.Max(1536, (int)(availRamMb * 0.85));
        int totalBasedCap = Math.Min((int)(totalRamMb * maxShare), Math.Max(1536, totalRamMb - osReserveMb));
        int hardSafeCap = Math.Min(totalBasedCap, Math.Max(freeBasedCap, Math.Min(totalBasedCap, 3072)));

        // G1GC suffers longer STW pauses beyond ~8-10 GB; ZGC / Gen-ZGC scales smoothly up to 12 GB+ on heavy packs
        int softGcCap = usesZgc
            ? ((isModpack || safeMods >= 180) ? 12288 : 8192)
            : ((isModpack || safeMods >= 150) ? 10240 : 8192);
        int capped = Math.Min(desiredMb, Math.Min(hardSafeCap, softGcCap));
        int aligned = (int)(Math.Round(capped / 512.0) * 512);
        return Math.Clamp(aligned, 1024, 16384);
    }

    /// <summary>
    /// Selects a fast-boot initial heap (-Xms) so large modpacks do not stall for seconds
    /// zero-committing 8-12 GB of OS pages before Minecraft even creates its window.
    /// </summary>
    public static int ComputeInitialHeapMb(int memoryMb, int availRamMb, bool usesZgc)
    {
        int cap = memoryMb >= 6144 ? 1024 : 512;
        return Math.Clamp(Math.Min(memoryMb, cap), 256, memoryMb);
    }

    private static int ParseMcMinor(string? mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return 21;
        var clean = mcVersion.Split('-', '_')[0];
        var parts = clean.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor))
        {
            return major > 1 ? 26 : minor;
        }
        return 21;
    }
}
