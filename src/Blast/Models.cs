using System.Text.Json.Serialization;

namespace Blast;

internal enum Presence { Present, Absent, Unknown }
internal enum CoverageFailure { ReadFailure, UnsupportedType }
internal enum ChangeKind { Modified, Added, Deleted, Renamed, Unsupported }

internal sealed record ProtectionScope(string Id, string Root, string Kind, string? SelectedFile = null,
    uint? RootVolume = null, ulong? RootId = null);

internal sealed record FileState(
    Presence Presence,
    string RelativePath,
    string? ContentHash,
    string? ObjectId,
    long Length,
    uint Volume,
    ulong FileId,
    uint Links,
    FileAttributes Attributes,
    uint ParentVolume,
    ulong ParentId,
    string? Error = null,
    CoverageFailure? FailureKind = null)
{
    internal static FileState Absent(string path, FileIdentity parent) =>
        new(Presence.Absent, path, null, null, 0, 0, 0, 0, 0, parent.Volume, parent.Index);

    internal static FileState Unknown(string path, string reason,
        CoverageFailure kind = CoverageFailure.ReadFailure) =>
        new(Presence.Unknown, path, null, null, 0, 0, 0, 0, 0, 0, 0, reason, kind);
}

internal sealed record ChangeRecord(
    string Id,
    ChangeKind Kind,
    string Path,
    string? Destination,
    FileState Baseline,
    FileState Final,
    string Attribution,
    string? UnsupportedReason = null,
    string? GroupId = null);

internal sealed class SessionRecord
{
    public required string Id { get; init; }
    public required string Root { get; init; }
    public List<ProtectionScope> Scopes { get; init; } = [];
    public required string Status { get; set; }
    public int? ChildExitCode { get; set; }
    public int? ChildProcessId { get; set; }
    public DateTimeOffset? FinalScanCompletedUtc { get; set; }
    public int CancelRequests { get; set; }
    public string ConsoleControlMode { get; set; } = "unknown";
    public string? InterruptionReason { get; set; }
    public required Dictionary<string, FileState> Baseline { get; init; }
    public Dictionary<string, FileState>? Final { get; set; }
    public Dictionary<string, FileIdentity> BaselineDirectories { get; set; } = [];
    public Dictionary<string, FileIdentity>? FinalDirectories { get; set; }
    public List<ChangeRecord>? Changes { get; set; }
    public string RuleVersion { get; init; } = "synthetic-v2-explicit-git-env";
    public string Coverage { get; set; } = "unknown";
    public CoverageRecord CoverageDetail { get; set; } = new();
}

internal sealed class CoverageRecord
{
    public List<string> ConfirmedExclusions { get; set; } = [];
    public List<string> ReadFailures { get; set; } = [];
    public List<string> UnsupportedTypes { get; set; } = [];
    public List<string> CompleteBaselines { get; set; } = [];
    public string? ScanFailure { get; set; }
}

internal sealed class RestoreOperation
{
    public required string ChangeId { get; init; }
    public required string Status { get; set; }
    public Presence ExpectedPrePresence { get; init; }
    public uint ExpectedPreParentVolume { get; init; }
    public ulong ExpectedPreParentId { get; init; }
    public Presence ExpectedPostPresence { get; init; }
    public string? ExpectedPostHash { get; init; }
    public long ExpectedPostLength { get; init; }
    public string? SafetyObjectId { get; set; }
    public string? Message { get; set; }
    public Presence? ActualPresence { get; set; }
    public string? ActualContentHash { get; set; }
    public uint? ActualVolume { get; set; }
    public ulong? ActualFileId { get; set; }
    public uint? ActualParentVolume { get; set; }
    public ulong? ActualParentId { get; set; }
}

internal sealed class RestorePlan
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string Hash { get; init; }
    public required string ExecutionPayload { get; init; }
    public required List<string> ChangeIds { get; init; }
    public required string Status { get; set; }
    public required List<RestoreOperation> Operations { get; init; }
}

internal sealed record PlanExecutionPayload(
    int SchemaVersion,
    string PlanId,
    string SessionId,
    string RuleVersion,
    string Root,
    List<ProtectionScope> Scopes,
    SortedDictionary<string, FileIdentity> BaselineDirectories,
    List<string> SelectedChangeIds,
    List<ChangeRecord> Changes);

internal sealed record ApplyResult(string Status, int OperationsApplied, IReadOnlyList<RestoreOperation> Operations);

internal sealed record RunResult(string SessionId, string BlastStatus, int? ChildExitCode,
    int? ChildProcessId = null, int CancelRequests = 0, string ConsoleControlMode = "unknown")
{
    // Callers must also inspect BlastStatus; child and Blast integer codes may collide.
    internal int WrapperExitCode => BlastStatus == "ok" && ChildExitCode is int code ? code : 70;
}

internal sealed record ObjectAudit(IReadOnlyList<string> PendingEncryptedFiles,
    IReadOnlyList<string> UnreferencedPublishedObjects);
