namespace DCL_Launcher;

internal static class DownloadRoutes
{
    private const string BmclApi = "https://bmclapi2.bangbang93.com";
    private const string Mcim = "https://mod.mcimirror.top";

    internal static bool IsMirror(string host) => host is "bmclapi2.bangbang93.com" or "mod.mcimirror.top";

    internal static IReadOnlyList<string> Files(string url, string source, bool verified)
    {
        if (!verified || source == "官方源" || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return [url];

        bool useBmcl = source is "BMCLAPI" or "最优" or "MCIM";
        bool useMcim = source is "MCIM" or "最优" or "BMCLAPI";
        string? mirror = uri.Host switch
        {
            "piston-meta.mojang.com" or "launchermeta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com" when useBmcl => BmclApi,
            "resources.download.minecraft.net" when useBmcl => BmclApi + "/assets",
            "libraries.minecraft.net" when useBmcl => BmclApi + "/maven",
            "cdn.modrinth.com" or "edge.forgecdn.net" or "mediafilez.forgecdn.net" when useMcim => Mcim,
            _ => null
        };
        return mirror == null ? [url] : [mirror + uri.PathAndQuery, url];
    }
}
