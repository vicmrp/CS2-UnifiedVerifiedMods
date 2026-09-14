public sealed class UvmReleaseManifest
{
    public int Schema { get; set; }

    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string Commit { get; set; } = "";

    public GithubReleaseInfo GithubRelease { get; set; } =
        new();

    public ParadoxReleaseInfo Paradox { get; set; } =
        new();

    public DateTime GeneratedUtc { get; set; }

    public SortedDictionary<string, ManifestFile> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GithubReleaseInfo
{
    public string Tag { get; set; } = "";
}

public sealed class ParadoxReleaseInfo
{
    public string? ModId { get; set; }

    public string DisplayName { get; set; } = "";

    public string ModVersion { get; set; } = "";

    public string GameVersion { get; set; } = "";
}