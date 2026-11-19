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
    /// loader overhead, MC version generation, mod count, shaderpacks, and modpack heuristics
    /// (informed by HMCL/PCL2 tier limits & GC pause safety caps).
    /// </summary>
    public static int ComputeRecommendedMemoryMb(
        int totalRamMb,
        int availRamMb,
        string? mcVersion,
        string? loader,
        int modCount,
        bool isModpack,
        int shaderCount = 0)
    {
        int mcMinor = ParseMcMinor(mcVersion);
        bool isModern = mcMinor >= 18;
        bool isMid = mcMinor >= 13 && mcMinor < 18;
        bool isVanilla = string.IsNullOrWhiteSpace(loader) || loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase);
        bool isHeavyLoader = !isVanilla && (
            loader!.Equals("Forge", StringComparison.OrdinalIgnoreCase) ||
            loader.Equals("NeoForge", StringComparison.OrdinalIgnoreCase));

        int baseMb;
        if (isVanilla)
            baseMb = isModern ? 2560 : isMid ? 2048 : 1536;
        else if (isHeavyLoader)
            baseMb = isModern ? 3584 : isMid ? 3072 : 2560;
        else
            baseMb = isModern ? 3072 : 2048;

        int safeMods = Math.Max(0, modCount);
        int modExtraMb = safeMods > 0
            ? Math.Min(5632, (int)Math.Round(safeMods * (isHeavyLoader ? 28.0 : 22.0)))
            : 0;
        int shaderExtraMb = shaderCount > 0 ? 512 : 0;

        int desiredMb = baseMb + modExtraMb + shaderExtraMb;
        if (isModpack)
            desiredMb = Math.Max(desiredMb + 512, safeMods >= 200 ? 8192 : 6144);
        else if (safeMods >= 150)
            desiredMb = Math.Max(desiredMb, 6144);

        // Leave safe headroom for OS & WebView2 without starving small RAM machines
        int osReserveMb = totalRamMb >= 16384 ? 3072 : totalRamMb >= 8192 ? 2048 : 1536;
        int freeBasedCap = Math.Max(1536, (int)(availRamMb * 0.85));
        int totalBasedCap = Math.Min((int)(totalRamMb * 0.65), Math.Max(1536, totalRamMb - osReserveMb));
        int hardSafeCap = Math.Min(totalBasedCap, Math.Max(freeBasedCap, Math.Min(totalBasedCap, 3072)));

        // Prevent excessive GC stop-the-world pauses unless heavy modpack needs it
        int softGcCap = (isModpack || safeMods >= 150) ? 10240 : 8192;
        int capped = Math.Min(desiredMb, Math.Min(hardSafeCap, softGcCap));
        int aligned = (int)(Math.Round(capped / 512.0) * 512);
        return Math.Clamp(aligned, 1024, 16384);
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
