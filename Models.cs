namespace WingetX;

internal sealed class TrackingDocument
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<TrackedPackage> Packages { get; set; } = [];
}

internal sealed class TrackedPackage
{
    public required string PackageIdentifier { get; set; }
    public string? Source { get; set; }
    public string? Architecture { get; set; }
    public string? Scope { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string AddedBy { get; set; } = "install";

    public string Key =>
        $"{PackageIdentifier}\u001f{Source ?? ""}\u001f{Architecture ?? ""}\u001f{Scope ?? ""}";
}

internal sealed record ExportedPackage(string PackageIdentifier, string? Source)
{
    public string Key => $"{PackageIdentifier}\u001f{Source ?? ""}";
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class WingetExitCodes
{
    public const int Success = 0;
    public const int InternalError = unchecked((int)0x8A150001);
    public const int InvalidArguments = unchecked((int)0x8A150002);
    public const int CommandFailed = unchecked((int)0x8A150003);
    public const int NoApplicationsFound = unchecked((int)0x8A150014);
    public const int NoManifestFound = unchecked((int)0x8A150017);
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    public const int UpdateAllHasFailure = unchecked((int)0x8A15002C);
    public const int UpgradeVersionNotNewer = unchecked((int)0x8A15004F);
    public const int MultipleInstallFailed = unchecked((int)0x8A150065);
    public const int MultipleUninstallFailed = unchecked((int)0x8A150066);
}
