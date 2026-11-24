namespace RemoteDesk;

internal static class EngineCatalog
{
    private static readonly EngineDescriptor SunshineDescriptor = new(
        Kind: EngineKind.Sunshine,
        Id: "sunshine",
        DisplayName: "Sunshine",
        RoleTag: "高刷被控端",
        Description: "开源自托管串流服务端，支持 NVENC / QSV / AMF 硬件编码，与内置高刷控屏引擎配合使用。",
        License: "GPL-3.0",
        HomepageUrl: "https://github.com/LizardByte/Sunshine",
        ReleasesApiUrl: "https://api.github.com/repos/LizardByte/Sunshine/releases/latest",
        ReleasesPageUrl: "https://github.com/LizardByte/Sunshine/releases/latest",
        ExecutableName: "sunshine.exe",
        PreferredAssetKeywords: ["sunshine-windows-portable", ".zip"],
        ExcludedAssetKeywords: ["arm64", "installer", ".exe", "debug"]);

    private static readonly EngineDescriptor EasyTierDescriptor = new(
        Kind: EngineKind.EasyTier,
        Id: "easytier",
        DisplayName: "EasyTier",
        RoleTag: "跨网 P2P 组网",
        Description: "轻量开源 P2P 虚拟组网引擎，同校园网自动直连，跨外网打洞与阿里云中继兜底。",
        License: "Apache-2.0",
        HomepageUrl: "https://github.com/EasyTier/EasyTier",
        ReleasesApiUrl: "https://api.github.com/repos/EasyTier/EasyTier/releases/latest",
        ReleasesPageUrl: "https://github.com/EasyTier/EasyTier/releases/latest",
        ExecutableName: "easytier-core.exe",
        PreferredAssetKeywords: ["easytier-windows-x86_64", ".zip"],
        ExcludedAssetKeywords: ["arm64", "aarch64", "linux", "macos", "android", "sha256", "gui"],
        IsPhase2Placeholder: false);

    public static IReadOnlyList<EngineDescriptor> All { get; } =
    [
        SunshineDescriptor,
        EasyTierDescriptor,
    ];

    public static EngineDescriptor Get(EngineKind kind) => kind switch
    {
        EngineKind.Sunshine => SunshineDescriptor,
        EngineKind.EasyTier => EasyTierDescriptor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的引擎类型。"),
    };

    public static string ApplyMirror(string url, string? mirrorPrefix)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(mirrorPrefix))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var sourceUri) ||
            !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !sourceUri.IsDefaultPort ||
            !string.IsNullOrEmpty(sourceUri.UserInfo))
        {
            return url;
        }

        var trimmedPrefix = mirrorPrefix.Trim();
        if (trimmedPrefix.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return url;

        if (!Uri.TryCreate(trimmedPrefix, UriKind.Absolute, out var mirrorUri) ||
            !string.Equals(mirrorUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(mirrorUri.Host) ||
            Uri.CheckHostName(mirrorUri.Host) == UriHostNameType.Unknown ||
            !string.IsNullOrEmpty(mirrorUri.UserInfo) ||
            !string.IsNullOrEmpty(mirrorUri.Query) ||
            !string.IsNullOrEmpty(mirrorUri.Fragment) ||
            string.Equals(mirrorUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var normalizedPrefix = trimmedPrefix.EndsWith('/') ? trimmedPrefix : trimmedPrefix + "/";
        var rewritten = normalizedPrefix + url;
        if (!Uri.TryCreate(rewritten, UriKind.Absolute, out var rewrittenUri) ||
            !string.Equals(rewrittenUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rewrittenUri.Host, mirrorUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return rewritten;
    }

    public static (string Name, string DownloadUrl)? SelectBestAsset(
        EngineDescriptor descriptor,
        IEnumerable<(string Name, string DownloadUrl)> assets)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assets);

        var keywordSets = GetKeywordSets(descriptor);
        (string Name, string DownloadUrl)? best = null;
        int bestTier = int.MaxValue;
        int bestScore = int.MinValue;

        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(asset.DownloadUrl))
                continue;

            var name = asset.Name.Trim();
            var downloadUrl = asset.DownloadUrl.Trim();

            if (!IsSupportedPackageExtension(name))
                continue;

            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsExcluded(name, descriptor.ExcludedAssetKeywords))
                continue;

            var tier = MatchTier(name, keywordSets);
            if (tier < 0)
                continue;

            var score = ComputeTieBreakScore(name);
            if (best is null ||
                tier < bestTier ||
                (tier == bestTier && score > bestScore) ||
                (tier == bestTier && score == bestScore && name.Length < best.Value.Name.Length) ||
                (tier == bestTier && score == bestScore && name.Length == best.Value.Name.Length &&
                 StringComparer.OrdinalIgnoreCase.Compare(name, best.Value.Name) < 0))
            {
                best = (name, downloadUrl);
                bestTier = tier;
                bestScore = score;
            }
        }

        return best;
    }

    private static IReadOnlyList<string[]> GetKeywordSets(EngineDescriptor descriptor)
    {
        var sets = new List<string[]>();
        if (descriptor.PreferredAssetKeywords is { Length: > 0 })
            sets.Add(descriptor.PreferredAssetKeywords);

        switch (descriptor.Kind)
        {
            case EngineKind.Sunshine:
                sets.Add(["windows", "portable", ".zip"]);
                break;
            case EngineKind.EasyTier:
                sets.Add(["windows", "x86_64", ".zip"]);
                break;
        }

        return sets;
    }

    private static bool IsSupportedPackageExtension(string fileName) =>
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcluded(string fileName, string[]? excludedKeywords)
    {
        if (excludedKeywords is null || excludedKeywords.Length == 0)
            return false;

        foreach (var keyword in excludedKeywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                continue;

            if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int MatchTier(string fileName, IReadOnlyList<string[]> keywordSets)
    {
        for (var i = 0; i < keywordSets.Count; i++)
        {
            var set = keywordSets[i];
            if (set.Length == 0)
                continue;

            var allMatched = true;
            foreach (var keyword in set)
            {
                if (!MatchesKeyword(fileName, keyword))
                {
                    allMatched = false;
                    break;
                }
            }

            if (allMatched)
                return i;
        }

        return -1;
    }

    private static bool MatchesKeyword(string fileName, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        return keyword.StartsWith('.')
            ? fileName.EndsWith(keyword, StringComparison.OrdinalIgnoreCase)
            : fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static int ComputeTieBreakScore(string fileName)
    {
        var score = 0;
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            score += 100;
        if (fileName.Contains("portable", StringComparison.OrdinalIgnoreCase))
            score += 30;
        if (fileName.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("amd64", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }
        return score;
    }
}
