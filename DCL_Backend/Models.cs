using System.Text.Json.Serialization;

namespace DCL_Launcher;

public sealed class DclConfig
{
    public string ActiveInstanceId { get; set; } = "";
    public string MirrorSource { get; set; } = "BMCLAPI";
    public bool AutoMirror { get; set; } = true;
    public bool AutoMemory { get; set; } = true;
    public int MemoryMb { get; set; } = 4096;
    public string JvmPreset { get; set; } = "Gen-ZGC (Java 21)";
    public string CustomJvmArgs { get; set; } = "-XX:+UseZGC -XX:+ZGenerational -Dfile.encoding=UTF-8";
    public string ThemeId { get; set; } = "obsidian-yellow";
    public string BgPatternId { get; set; } = "none";
    public bool AlwaysOnTop { get; set; } = false;
    public string ActiveJavaId { get; set; } = "";
    public string MicrosoftClientId { get; set; } = "";
    public string CurseForgeApiKey { get; set; } = "";
    public string CurseForgeApiRoute { get; set; } = "auto";
}

public sealed class InstanceMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "1.21.1";
    public string Loader { get; set; } = "Vanilla";
    public string LoaderVersion { get; set; } = "";
    public string JavaId { get; set; } = "";
    public string JavaVersion { get; set; } = "Java 21";
    public string JvmArgs { get; set; } = "";
    public string IconId { get; set; } = "grass";
    public string? CustomIcon { get; set; }
    public int RequiredJavaMajor { get; set; }
    public bool IsModpack { get; set; }
    public string FolderColor { get; set; } = "amber";
    public string? CustomFolderHex { get; set; }
    public bool Favorite { get; set; }
    public int ModsCount { get; set; }
    public string LastPlayed { get; set; } = "未游玩";
    public DateTimeOffset? LastPlayedAt { get; set; }
    public bool Isolated { get; set; } = false;
    public bool UseGlobalSettings { get; set; } = true;
    public bool AutoMemory { get; set; } = true;
    public int MemoryMb { get; set; } = 4096;
    public string Tagline { get; set; } = "";
    public string? ExternalPath { get; set; }
    public string? ExternalMinecraftDir { get; set; }
}

public sealed class AccountEntry
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Type { get; set; } = "离线账号";
    public string Avatar { get; set; } = "assets/mc-icons/player-head.png";
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
}

public sealed class AccountsStore
{
    public string ActiveAccountId { get; set; } = "";
    public List<AccountEntry> Accounts { get; set; } = new();
}

public sealed class JavaInstallation
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Arch { get; set; } = "x64";
    public string Path { get; set; } = "";
    public bool Active { get; set; }
    internal string[] LegacyIds { get; init; } = [];
    internal bool MatchesId(string? id) => id != null && (Id == id || LegacyIds.Contains(id, StringComparer.Ordinal));
}

public sealed class ModEntry
{
    public string Id { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CnName { get; set; } = "";
    public string Pinyin { get; set; } = "";
    public string Version { get; set; } = "";
    public string File { get; set; } = "";
    public string Icon { get; set; } = "assets/mc-icons/cobblestone.png";
    public string Desc { get; set; } = "";
    public bool Enabled { get; set; }
    public bool HasUpdate { get; set; }
    public string Latest { get; set; } = "";
}

public sealed class PackEntry
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "资源包";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Icon { get; set; } = "assets/mc-icons/grass.png";
    public string Spec { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class DownloadTaskEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Icon { get; set; } = "assets/mc-icons/grass.png";
    public string Source { get; set; } = "BMCLAPI";
    public string Size { get; set; } = "";
    public string Speed { get; set; } = "";
    public int Progress { get; set; }
    public string Status { get; set; } = "downloading";
}
