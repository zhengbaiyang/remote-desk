namespace RemoteDesk;

internal enum EngineKind
{
    Sunshine,
    EasyTier,
}

internal enum EngineInstallState
{
    NotInstalled,
    SystemInstalled,
    ManagedInstalled,
    Installing,
    PlannedPhase2,
}

internal sealed record EngineDescriptor(
    EngineKind Kind,
    string Id,
    string DisplayName,
    string RoleTag,
    string Description,
    string License,
    string HomepageUrl,
    string ReleasesApiUrl,
    string ReleasesPageUrl,
    string ExecutableName,
    string[] PreferredAssetKeywords,
    string[] ExcludedAssetKeywords,
    bool IsPhase2Placeholder = false);

internal sealed record EngineStatus(
    EngineDescriptor Descriptor,
    EngineInstallState State,
    string? ExecutablePath,
    string? InstalledVersion,
    string? DetailMessage = null);

internal sealed record EngineInstallProgress(
    EngineKind Kind,
    string Stage,
    double? Percentage);
