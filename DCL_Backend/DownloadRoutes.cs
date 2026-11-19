using System.Collections;
using System.Collections.Concurrent;
using System.Net;

namespace DCL_Launcher;

internal sealed record RouteList(string[] Addresses, bool Automatic) : IReadOnlyList<string>
{
    public int Count => Addresses.Length;
    public string this[int index] => Addresses[index];
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)Addresses).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class DownloadRoutes
{
    private const string Bmcl = "https://bmclapi2.bangbang93.com";
    private const string Mcim = "https://mod.mcimirror.top";
    internal static bool IsMirror(string host) => host is "bmclapi2.bangbang93.com" or "mod.mcimirror.top";

    internal static IReadOnlyList<string> Files(string url, string source, bool verified)
    {
        if (!verified || source == "官方源" || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return [url];
        string? mirror = uri.Host switch
        {
            "piston-meta.mojang.com" or "launchermeta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com" => Bmcl,
            "resources.download.minecraft.net" => Bmcl + "/assets",
            "libraries.minecraft.net" or "maven.fabricmc.net" or "maven.minecraftforge.net" or "maven.neoforged.net" => Bmcl + "/maven",
            "meta.fabricmc.net" => Bmcl + "/fabric-meta",
            "api.modrinth.com" => Mcim + "/modrinth",
            "cdn.modrinth.com" or "edge.forgecdn.net" or "mediafilez.forgecdn.net" => Mcim,
            _ => null
        };
        if (mirror == null) return [url];
        string pathAndQuery = uri.Host == "maven.neoforged.net" && uri.PathAndQuery.StartsWith("/releases/", StringComparison.OrdinalIgnoreCase)
            ? uri.PathAndQuery["/releases".Length..]
            : uri.PathAndQuery;
        bool auto = source.StartsWith("最优", StringComparison.Ordinal);
        bool officialFirst = source == "最优/官方源";
        return new RouteList(officialFirst ? [url, mirror + pathAndQuery] : [mirror + pathAndQuery, url], auto);
    }

    internal static string Family(Uri u) => u.Host switch
    {
        "bmclapi2.bangbang93.com" when u.AbsolutePath.StartsWith("/assets/") => "assets",
        "resources.download.minecraft.net" => "assets",
        "bmclapi2.bangbang93.com" when u.AbsolutePath.StartsWith("/maven/") => "libraries",
        "libraries.minecraft.net" or "maven.fabricmc.net" or "maven.minecraftforge.net" or "maven.neoforged.net" => "libraries",
        "mod.mcimirror.top" when !u.AbsolutePath.StartsWith("/modrinth/") && !u.AbsolutePath.StartsWith("/curseforge/") => "mods",
        "cdn.modrinth.com" or "edge.forgecdn.net" or "mediafilez.forgecdn.net" => "mods",
        "piston-data.mojang.com" or "launcher.mojang.com" => "client",
        "bmclapi2.bangbang93.com" when u.AbsolutePath.EndsWith(".jar") => "client",
        _ => "metadata"
    };
}

internal sealed class NetworkHealth
{
    internal sealed class Host
    {
        internal readonly SemaphoreSlim Slots = new(8);
        internal readonly SemaphoreSlim Recovery = new(1);
        internal long CoolUntil;
        internal int Recovering;
        internal double Availability, Mbps, Latency;
        internal int Samples;
        internal readonly Dictionary<string, double> Scores = new();
        internal readonly Dictionary<string, long> RetryAt = new();
    }
    private readonly ConcurrentDictionary<string, Host> _hosts = new(StringComparer.OrdinalIgnoreCase);
    internal Host For(string url) => _hosts.GetOrAdd(new Uri(url).Host, _ => new Host());
    internal (int Ping, double Available, double Mbps) Metrics(string url)
    {
        var h = For(url);
        lock (h) return (h.Samples == 0 ? 9999 : (int)h.Latency, h.Availability, h.Mbps);
    }
    internal long WaitMs(string url) => Math.Max(0, Interlocked.Read(ref For(url).CoolUntil) - Environment.TickCount64);
    internal string[] Order(IReadOnlyList<string> urls)
    {
        double Score(string url)
        {
            var h = For(url);
            lock (h) return h.Scores.GetValueOrDefault(DownloadRoutes.Family(new Uri(url)),
                h.Samples > 0 ? h.Latency + 1000 * (1 - h.Availability) : 500);
        }
        bool auto = urls is RouteList { Automatic: true };
        var ordered = urls.Distinct().OrderBy(u => WaitMs(u) > 0).ThenBy(u => auto ? Score(u) : 0).ToList();
        if (auto && ordered.Count > 1)
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                string url = ordered[i], family = DownloadRoutes.Family(new Uri(url));
                var host = For(url);
                lock (host)
                {
                    long now = Environment.TickCount64;
                    if (host.CoolUntil > now || !host.RetryAt.TryGetValue(family, out long due) || due > now) continue;
                    host.RetryAt[family] = now + 15000;
                }
                ordered.RemoveAt(i);
                ordered.Insert(0, url);
                break;
            }
        }
        return ordered.ToArray();
    }
    internal void Success(string url, long elapsed, long length, double firstByte = -1)
    {
        var h = For(url);
        double latency = firstByte >= 0 ? firstByte : elapsed;
        lock (h)
        {
            h.Latency = h.Samples == 0 ? latency : .75 * h.Latency + .25 * latency;
            h.Availability = h.Samples++ == 0 ? 1 : .85 * h.Availability + .15;
            if (length >= 32768 && elapsed > 0)
            {
                double rate = length * .008 / elapsed;
                h.Mbps = h.Mbps == 0 ? rate : .75 * h.Mbps + .25 * rate;
            }
            string family = DownloadRoutes.Family(new Uri(url));
            h.RetryAt.Remove(family);
            double score = latency + (h.Mbps > 0 && family != "metadata" ? 524 / h.Mbps : 0);
            h.Scores[family] = .6 * h.Scores.GetValueOrDefault(family, score) + .4 * score;
            if (WaitMs(url) == 0) h.Recovering = 0;
        }
    }
    internal void Failure(string url, Exception error, TimeSpan? retry = null)
    {
        if (error is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.RequestedRangeNotSatisfiable }) return;
        var h = For(url);
        lock (h)
        {
            h.Availability *= .8;
            if (h.Samples++ == 0) h.Latency = 9999;
            string family = DownloadRoutes.Family(new Uri(url));
            h.Scores[family] = Math.Max(1800, h.Scores.GetValueOrDefault(family)) + 400;
            h.CoolUntil = Math.Max(h.CoolUntil, Environment.TickCount64 + (long)Math.Max(0, (retry ?? TimeSpan.FromSeconds(4)).TotalMilliseconds));
            h.RetryAt[family] = h.CoolUntil;
            h.Recovering = 1;
        }
    }
    internal sealed class CoolingException(string message = "节点正在冷却") : HttpRequestException(message) { }
    internal async Task<IDisposable> EnterAsync(string url, CancellationToken ct, bool waitForCooling = true)
    {
        var h = For(url);
        await h.Slots.WaitAsync(ct);
        bool recovery = false;
        try
        {
            while (true)
            {
                long wait = WaitMs(url);
                if (wait > 0 && !waitForCooling) throw new CoolingException();
                if (wait > 60000) throw new CoolingException($"节点限流，请在 {Math.Ceiling(wait / 1000d):0} 秒后继续");
                if (wait > 0) await Task.Delay((int)Math.Min(wait, 60000), ct);
                else if (!recovery && Volatile.Read(ref h.Recovering) != 0)
                {
                    await h.Recovery.WaitAsync(ct);
                    recovery = true;
                }
                else break;
            }
            return new Lease(h, recovery);
        }
        catch { if (recovery) h.Recovery.Release(); h.Slots.Release(); throw; }
    }
    private sealed class Lease(Host h, bool recovery) : IDisposable
    {
        public void Dispose() { if (recovery) h.Recovery.Release(); h.Slots.Release(); }
    }
}
