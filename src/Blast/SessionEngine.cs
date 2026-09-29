using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Blast;

internal sealed class SessionEngine
{
    private readonly SyntheticFixture fixture;
    private readonly StateStore state;
    private readonly ObjectStore objects;
    internal Action<string>? BoundaryForTest { get; set; }

    internal SessionEngine(SyntheticFixture fixture)
    {
        this.fixture = fixture;
        state = new StateStore(fixture.StateDirectory);
        using (state.AcquireLock()) objects = new ObjectStore(fixture.StateDirectory,
            allowNewKey: !state.HasStoredRecords());
    }

    internal StateStore State => state;
    internal ObjectStore Objects => objects;

    internal RunResult Run(ProcessStartInfo command, CancellationToken cancellationToken = default)
    {
        using var storeLock = state.AcquireLock();
        using var cancellation = new RunCancellation(cancellationToken);
        var attempt = new RunAttempt();
        try { return RunLocked(command, cancellation, attempt); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or
                                   System.ComponentModel.Win32Exception)
        {
            // A second write may fail too. Do not recurse into SaveSession or claim
            // that an in-memory failure status was durably recorded.
            var observed = attempt.Session;
            return new RunResult(observed?.Id ?? "", "run_failed", observed?.ChildExitCode,
                observed?.ChildProcessId, cancellation.Requests, cancellation.ConsoleControlMode,
                DiagnosticPersisted: false, Error: ex.GetType().Name + ": " + ex.Message);
        }
    }

    private sealed class RunAttempt { internal SessionRecord? Session; }

    private RunResult RunLocked(ProcessStartInfo command, RunCancellation cancellation, RunAttempt attempt)
    {
        state.AuditUnresolved();
        if (state.HasUnresolvedPlans())
            throw new InvalidOperationException("Unresolved restore intent blocks a new run in this state store.");
        if (state.HasUnresolvedRuns())
            throw new InvalidOperationException("Unresolved run blocks new run in this state store.");
        ValidateScopes();
        NormalizeCommand(command);
        string id = Guid.NewGuid().ToString("N");
        var coverage = new CoverageRecord();
        var baselineDirectories = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FileState> baseline;
        try
        {
            BoundaryForTest?.Invoke("before_baseline");
            cancellation.ThrowIfRequested();
            baseline = CaptureScopes(fixture.Scopes, coverage, baselineDirectories);
            cancellation.ThrowIfRequested();
        }
        catch (OperationCanceledException)
        {
            coverage.ScanFailure = "Run cancelled before baseline became ready.";
            var interrupted = new SessionRecord
            {
                Id = id, Root = fixture.Root, Scopes = [.. fixture.Scopes], Status = "interrupted",
                Baseline = [], Coverage = "incomplete", CoverageDetail = coverage,
                ChildLaunchState = "not_started",
                ConsoleControlMode = cancellation.ConsoleControlMode,
                CancelRequests = cancellation.Requests,
                InterruptionReason = "cancelled_during_baseline"
            };
            attempt.Session = interrupted;
            state.SaveSession(interrupted);
            return RunOutcome(interrupted, "interrupted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            coverage.ScanFailure = ex.GetType().Name + ": " + ex.Message;
            var failed = new SessionRecord
            {
                Id = id, Root = fixture.Root, Scopes = [.. fixture.Scopes], Status = "failed", Baseline = [],
                Coverage = "failed", CoverageDetail = coverage,
                ChildLaunchState = "not_started",
                ConsoleControlMode = cancellation.ConsoleControlMode,
                CancelRequests = cancellation.Requests
            };
            attempt.Session = failed;
            state.SaveSession(failed);
            return RunOutcome(failed, "baseline_failed");
        }
        var boundScopes = fixture.Scopes.Select(scope =>
        {
            var identity = baselineDirectories[DirectoryKey(scope, ".")];
            return scope with { RootVolume = identity.Volume, RootId = identity.Index };
        }).ToList();
        var session = new SessionRecord
        {
            Id = id, Root = fixture.Root, Scopes = boundScopes, Status = "baselining", Baseline = baseline,
            BaselineDirectories = baselineDirectories,
            ChildLaunchState = "not_started",
            ConsoleControlMode = cancellation.ConsoleControlMode,
            Coverage = baseline.Values.Any(x => x.Presence == Presence.Unknown) ? "failed" : "complete",
            CoverageDetail = coverage
        };
        attempt.Session = session;
        state.SaveSession(session);
        try
        {
            BoundaryForTest?.Invoke("baseline_saved");
            cancellation.ThrowIfRequested();
        }
        catch (OperationCanceledException)
        {
            return InterruptRun(session, cancellation, "cancelled_during_baseline");
        }
        if (session.Coverage != "complete")
        {
            session.Status = "failed";
            state.SaveSession(session);
            return RunOutcome(session, "baseline_failed");
        }
        try
        {
            ValidateHistoricalDirectories(session);
            foreach (var file in baseline.Values.Where(x => x.Presence == Presence.Present))
                objects.Verify(file.ObjectId!, file.Length);
            cancellation.ThrowIfRequested();
        }
        catch (OperationCanceledException)
        {
            return InterruptRun(session, cancellation, "cancelled_before_child_start");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            session.Status = "failed";
            session.Coverage = "failed";
            session.CoverageDetail.ScanFailure = "Baseline object verification: " + ex.GetType().Name;
            state.SaveSession(session);
            return RunOutcome(session, "baseline_failed");
        }
        session.Status = "ready";
        state.SaveSession(session);
        try
        {
            BoundaryForTest?.Invoke("ready");
            cancellation.ThrowIfRequested();
        }
        catch (OperationCanceledException)
        {
            return InterruptRun(session, cancellation, "cancelled_before_child_start");
        }

        try { cancellation.ThrowIfRequested(); }
        catch (OperationCanceledException)
        { return InterruptRun(session, cancellation, "cancelled_before_child_start"); }
        // This is the last durable step before Process.Start. A failed commit
        // cannot enter the launch catch, which conservatively treats Start itself
        // as uncertain; the prior durable ready state still proves nonlaunch.
        session.Status = "launch_pending";
        session.ChildLaunchState = "possible";
        state.SaveSession(session);
        try
        {
            BoundaryForTest?.Invoke("launch_pending_durable");
            cancellation.ThrowIfRequested();
            using var child = Process.Start(command) ?? throw new IOException("Child process did not start.");
            session.ChildProcessId = child.Id;
            session.Status = "running";
            state.SaveSession(session);
            BoundaryForTest?.Invoke("child_started");
            long? cancellationSeenAt = null;
            while (!child.WaitForExit(50))
            {
                if (cancellation.Requests == 0) continue;
                cancellationSeenAt ??= Environment.TickCount64;
                if (Environment.TickCount64 - cancellationSeenAt < 5000) continue;
                if (child.HasExited) break;
                return InterruptRun(session, cancellation, "child_did_not_exit_within_5s_after_cancel");
            }
            session.ChildExitCode = child.ExitCode;
            session.ChildLaunchState = "exited";
        }
        catch (OperationCanceledException)
        {
            return InterruptRun(session, cancellation,
                session.ChildProcessId is null ? "cancelled_before_child_start" : "cancelled_with_child_running");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // Process.Start can fail after the durable launch intent without a
            // returned handle. An absent PID is not evidence that no child ran.
            session.Status = "interrupted";
            session.Coverage = "incomplete";
            session.CoverageDetail.ScanFailure = "Child launch or wait failed: " + ex.GetType().Name;
            session.InterruptionReason = "launch_or_running_state_uncertain";
            session.CancelRequests = cancellation.Requests;
            state.SaveSession(session);
            return RunOutcome(session, "run_failed");
        }

        int requestsAtChildExit = cancellation.Requests;
        try
        {
            session.Status = "finalizing";
            state.SaveSession(session);
            BoundaryForTest?.Invoke("before_final_scan");
            if (cancellation.Requests != requestsAtChildExit)
                throw new OperationCanceledException("Cancellation requested after child exit.");
            var finalDirectories = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
            var final = CaptureScopes(session.Scopes, directories: finalDirectories);
            if (cancellation.Requests != requestsAtChildExit)
                throw new OperationCanceledException("Cancellation requested during final scan.");
            foreach (string path in baseline.Keys.Except(final.Keys, StringComparer.OrdinalIgnoreCase))
                final[path] = Capture(session, path).State;
            foreach (string path in final.Keys.Except(baseline.Keys, StringComparer.OrdinalIgnoreCase))
                baseline[path] = final[path].Presence == Presence.Present &&
                    HasHistoricalParent(session, finalDirectories, path)
                    ? FileState.Absent(path, new FileIdentity(final[path].ParentVolume, final[path].ParentId, 1, 0))
                    : FileState.Unknown(path, "Parent identity was not in the baseline.");
            session.Final = final;
            session.FinalDirectories = finalDirectories;
            bool directoriesChanged = !DirectoryHistoryMatches(session, finalDirectories);
            if (directoriesChanged)
                session.CoverageDetail.ScanFailure = "Protected directory identity changed or directory was added.";
            session.Coverage = directoriesChanged || final.Values.Any(x => x.Presence == Presence.Unknown)
                ? "incomplete" : "complete";
            session.Changes = BuildChanges(session);
            session.FinalScanCompletedUtc = DateTimeOffset.UtcNow;
            BoundaryForTest?.Invoke("before_final_commit");
            if (!cancellation.TryFinish(requestsAtChildExit))
                throw new OperationCanceledException("Cancellation requested before final commit.");
            // After this point, a new Ctrl+C may terminate Blast. A persisted
            // finalizing state remains safe if that happens before the commit.
            session.CancelRequests = cancellation.Requests;
            session.Status = session.Coverage == "complete" ? "complete" : "incomplete";
            state.SaveSession(session);
            return RunOutcome(session, session.Status == "complete" ? "ok" : "final_scan_incomplete");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or
                                   NotSupportedException or System.ComponentModel.Win32Exception or SqliteException)
        {
            bool interrupted = ex is OperationCanceledException || cancellation.Requests != requestsAtChildExit;
            session.Status = interrupted ? "interrupted" : "incomplete";
            session.Coverage = "incomplete";
            session.InterruptionReason = interrupted ? "cancelled_during_final_scan" : null;
            session.CoverageDetail.ScanFailure = ex.GetType().Name + ": " + ex.Message;
            session.CancelRequests = cancellation.Requests;
            state.SaveSession(session);
            return RunOutcome(session, interrupted ? "interrupted" : "final_scan_failed");
        }
    }

    private RunResult InterruptRun(SessionRecord session, RunCancellation cancellation, string reason)
    {
        if (session.ChildProcessId is null) session.ChildLaunchState = "not_started";
        session.Status = "interrupted";
        session.Coverage = "incomplete";
        session.InterruptionReason = reason;
        session.CoverageDetail.ScanFailure = reason;
        session.CancelRequests = cancellation.Requests;
        state.SaveSession(session);
        return RunOutcome(session, "interrupted");
    }

    private static RunResult RunOutcome(SessionRecord session, string blastStatus) =>
        new(session.Id, blastStatus, session.ChildExitCode, session.ChildProcessId,
            session.CancelRequests, session.ConsoleControlMode);

    private void NormalizeCommand(ProcessStartInfo command)
    {
        command.WorkingDirectory = fixture.Root;
        command.UseShellExecute = false;
        if (!command.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.IsNullOrEmpty(command.Arguments))
            throw new NotSupportedException("Raw cmd argument strings cannot be validated; use ArgumentList.");
        if (command.ArgumentList.Count == 0) return;
        // CreateProcess uses cmd.exe for batch files. Native argv backslash
        // quoting does not preserve cmd's argument semantics.
        string[] arguments = [.. command.ArgumentList];
        string encoded = string.Join(" ", arguments.Select(QuoteBatchArgument));
        command.ArgumentList.Clear();
        command.Arguments = encoded;
    }

    private static string QuoteBatchArgument(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '%', '!', '^', '&', '|', '<', '>', '"']) >= 0)
            throw new NotSupportedException("Batch argument contains unsupported cmd metacharacters.");
        return "\"" + value + "\"";
    }

    private void ValidateScopes()
    {
        string statePath = Path.GetFullPath(fixture.StateDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string workspaceVolume = Path.GetPathRoot(fixture.Root)!;
        uint workspaceVolumeId;
        using (var workspace = WindowsFiles.OpenDirectory(fixture.Root))
            workspaceVolumeId = WindowsFiles.Identity(workspace).Volume;
        foreach (var scope in fixture.Scopes)
        {
            if (!Path.GetPathRoot(scope.Root)!.Equals(workspaceVolume, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Cross-volume scope is not supported.");
            string root = Path.GetFullPath(scope.Root).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(root)) throw new InvalidOperationException("Scope root is missing: " + scope.Id);
            using (var scopeRoot = WindowsFiles.OpenDirectory(root))
            {
                var identity = WindowsFiles.Identity(scopeRoot);
                if (identity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    identity.Volume != workspaceVolumeId)
                    throw new NotSupportedException("Scope root is redirected or on another volume: " + scope.Id);
            }
            string protectedPath = scope.SelectedFile is null ? root : Path.Combine(root, scope.SelectedFile);
            if (protectedPath.Equals(statePath, StringComparison.OrdinalIgnoreCase) ||
                protectedPath.StartsWith(statePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                statePath.StartsWith(protectedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("State store intersects protected scope: " + scope.Id);
            if (scope.Id.Contains('|') || scope.Id == "workspace" && scope.Kind != "workspace")
                throw new InvalidOperationException("Invalid scope ID or kind.");
        }
        if (fixture.Scopes.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fixture.Scopes.Count)
            throw new InvalidOperationException("Duplicate scope ID.");
    }

    private Dictionary<string, FileState> CaptureScopes(IEnumerable<ProtectionScope> scopes,
        CoverageRecord? coverage = null, Dictionary<string, FileIdentity>? directories = null)
    {
        var result = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes)
        {
            Dictionary<string, FileState> captured;
            var detail = new CoverageRecord();
            var scopeDirectories = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
            if (scope.SelectedFile is not null)
            {
                using (var root = WindowsFiles.OpenDirectory(scope.Root))
                    scopeDirectories.Add(".", WindowsFiles.Identity(root));
                var file = FileSystemScope.Capture(scope.Root, scope.SelectedFile, objects).State;
                captured = new(StringComparer.OrdinalIgnoreCase) { [scope.SelectedFile] = file };
                if (file.Presence == Presence.Present) detail.CompleteBaselines.Add(scope.SelectedFile);
                else if (file.Presence == Presence.Unknown)
                {
                    if (file.FailureKind == CoverageFailure.UnsupportedType)
                        detail.UnsupportedTypes.Add(scope.SelectedFile);
                    else detail.ReadFailures.Add(scope.SelectedFile);
                }
            }
            else captured = FileSystemScope.CaptureManifest(scope.Root, objects, detail, scopeDirectories);
            if (directories is not null)
                foreach (var (relative, identity) in scopeDirectories)
                    directories.Add(DirectoryKey(scope, relative), identity);
            foreach (var (relative, file) in captured)
            {
                string key = ScopedKey(scope, relative);
                result.Add(key, file with { RelativePath = key });
            }
            if (coverage is not null)
            {
                coverage.ConfirmedExclusions.AddRange(detail.ConfirmedExclusions.Select(x => ScopedKey(scope, x)));
                coverage.ReadFailures.AddRange(detail.ReadFailures.Select(x => ScopedKey(scope, x)));
                coverage.UnsupportedTypes.AddRange(detail.UnsupportedTypes.Select(x => ScopedKey(scope, x)));
                coverage.CompleteBaselines.AddRange(detail.CompleteBaselines.Select(x => ScopedKey(scope, x)));
            }
        }
        return result;
    }

    private static string ScopedKey(ProtectionScope scope, string relative) =>
        scope.Id == "workspace" ? relative : scope.Id + "|" + relative;

    private static string DirectoryKey(ProtectionScope scope, string relative) => scope.Id + "|" + relative;

    private static bool DirectoryHistoryMatches(SessionRecord session,
        Dictionary<string, FileIdentity> observed)
    {
        if (session.BaselineDirectories.Count == 0 ||
            observed.Count != session.BaselineDirectories.Count) return false;
        foreach (var (key, expected) in session.BaselineDirectories)
            if (!observed.TryGetValue(key, out var actual) || actual != expected) return false;
        return session.Scopes.All(scope =>
            session.BaselineDirectories.TryGetValue(DirectoryKey(scope, "."), out var root) &&
            scope.RootVolume == root.Volume && scope.RootId == root.Index);
    }

    private static bool HasHistoricalParent(SessionRecord session,
        Dictionary<string, FileIdentity> finalDirectories, string key)
    {
        var (scope, relative) = Resolve(session, key);
        string? containing = Path.GetDirectoryName(relative);
        string parent = string.IsNullOrEmpty(containing) ? "." : containing;
        string directoryKey = DirectoryKey(scope, parent);
        return session.BaselineDirectories.TryGetValue(directoryKey, out var oldIdentity) &&
            finalDirectories.TryGetValue(directoryKey, out var newIdentity) &&
            oldIdentity == newIdentity;
    }

    private static void ValidateHistoricalDirectories(SessionRecord session)
    {
        if (session.BaselineDirectories.Count == 0)
            throw new InvalidOperationException("Session has no baseline directory identities.");
        foreach (var (key, expected) in session.BaselineDirectories)
        {
            int divider = key.IndexOf('|');
            var scope = session.Scopes.Single(s => s.Id == key[..divider]);
            string relative = key[(divider + 1)..];
            string path = relative == "." ? scope.Root : FileSystemScope.FullPath(scope.Root, relative);
            using var handle = WindowsFiles.OpenDirectory(path);
            if (WindowsFiles.Identity(handle) != expected)
                throw new InvalidOperationException("Protected directory identity changed: " + key);
        }
        foreach (var scope in session.Scopes)
        {
            var root = session.BaselineDirectories[DirectoryKey(scope, ".")];
            if (scope.RootVolume != root.Volume || scope.RootId != root.Index)
                throw new InvalidOperationException("Scope root binding changed: " + scope.Id);
        }
    }

    internal static (ProtectionScope Scope, string Relative) Resolve(SessionRecord session, string key)
    {
        int divider = key.IndexOf('|');
        string id = divider < 0 ? "workspace" : key[..divider];
        string relative = divider < 0 ? key : key[(divider + 1)..];
        var scope = session.Scopes.SingleOrDefault(s => s.Id == id)
            ?? throw new InvalidDataException("Unknown scope ID: " + id);
        if (scope.SelectedFile is not null &&
            !relative.Equals(scope.SelectedFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path is outside selected external file.");
        return (scope, relative);
    }

    internal static CapturedFile Capture(SessionRecord session, string key, ObjectStore? objects = null)
    {
        var (scope, relative) = Resolve(session, key);
        var captured = FileSystemScope.Capture(scope.Root, relative, objects);
        return captured with { State = captured.State with { RelativePath = key } };
    }

    private static List<ChangeRecord> BuildChanges(SessionRecord session)
    {
        var baseline = session.Baseline;
        var final = session.Final!;
        var result = new List<ChangeRecord>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A name exchange can leave both paths present. Treat identity cycles as one
        // unsupported relationship, never as two independent content modifications.
        foreach (var old in baseline.Values.Where(x => x.Presence == Presence.Present))
        {
            var now = final[old.RelativePath];
            if (now.Presence != Presence.Present || now.FileId == old.FileId) continue;
            bool identityAppearsAtAnotherPath = baseline.Values.Any(other =>
                other.RelativePath != old.RelativePath && other.Presence == Presence.Present &&
                final[other.RelativePath].Presence == Presence.Present &&
                ScopeId(other.RelativePath) == ScopeId(old.RelativePath) &&
                other.Volume == now.Volume && other.FileId == now.FileId);
            if (!identityAppearsAtAnotherPath) continue;
            result.Add(new(ChangeId(session.Id, ChangeKind.Unsupported, old.RelativePath, null),
                ChangeKind.Unsupported, old.RelativePath, null, old, now,
                "temporally_correlated", "unsupported_to_apply: name exchange or identity cycle."));
            used.Add(old.RelativePath);
        }

        foreach (var old in baseline.Values.Where(x => x.Presence == Presence.Present &&
                     final[x.RelativePath].Presence == Presence.Absent))
        {
            var occupiedDestination = final.Values.SingleOrDefault(x => x.Presence == Presence.Present &&
                x.RelativePath != old.RelativePath && ScopeId(x.RelativePath) == ScopeId(old.RelativePath) &&
                baseline[x.RelativePath].Presence == Presence.Present &&
                x.Volume == old.Volume && x.FileId == old.FileId);
            if (occupiedDestination is not null)
            {
                result.Add(new(ChangeId(session.Id, ChangeKind.Unsupported, old.RelativePath,
                        occupiedDestination.RelativePath), ChangeKind.Unsupported, old.RelativePath,
                    occupiedDestination.RelativePath, old, occupiedDestination, "temporally_correlated",
                    "unsupported_to_apply: overwrite rename or identity moved into an occupied path."));
                used.Add(old.RelativePath);
                used.Add(occupiedDestination.RelativePath);
                continue;
            }
            var destinations = final.Values.Where(x => x.Presence == Presence.Present &&
                baseline[x.RelativePath].Presence == Presence.Absent &&
                ScopeId(x.RelativePath) == ScopeId(old.RelativePath) &&
                x.Volume == old.Volume && x.FileId == old.FileId).ToList();
            if (destinations.Count != 1) continue;
            var destination = destinations[0];
            bool simple = Path.GetDirectoryName(old.RelativePath) == Path.GetDirectoryName(destination.RelativePath) &&
                old.ContentHash == destination.ContentHash && old.Security == destination.Security &&
                old.Attributes == destination.Attributes;
            var kind = simple ? ChangeKind.Renamed : ChangeKind.Unsupported;
            result.Add(new(ChangeId(session.Id, kind, old.RelativePath, destination.RelativePath), kind,
                old.RelativePath, destination.RelativePath, old, destination, "temporally_correlated",
                simple ? null : "unsupported_to_apply: complex rename or rename with content change."));
            used.Add(old.RelativePath);
            used.Add(destination.RelativePath);
        }

        foreach (string path in baseline.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (used.Contains(path)) continue;
            var before = baseline[path];
            var after = final[path];
            if (before.Presence == Presence.Unknown || after.Presence == Presence.Unknown)
            {
                result.Add(new(ChangeId(session.Id, ChangeKind.Unsupported, path, null),
                    ChangeKind.Unsupported, path, null, before, after, "unknown",
                    "unsupported_to_apply: unknown file state or missing parent."));
                continue;
            }
            if (before.Presence == after.Presence &&
                (before.Presence == Presence.Absent ||
                 (before.ContentHash == after.ContentHash && before.FileId == after.FileId &&
                  before.Security == after.Security && before.Attributes == after.Attributes))) continue;
            if (before.Presence == Presence.Present && after.Presence == Presence.Present &&
                (before.Security != after.Security || before.Attributes != after.Attributes))
            {
                result.Add(new(ChangeId(session.Id, ChangeKind.Unsupported, path, null),
                    ChangeKind.Unsupported, path, null, before, after, "temporally_correlated",
                    "unsupported_to_apply: permissions or attributes changed during the session."));
                continue;
            }
            var kind = before.Presence == Presence.Absent ? ChangeKind.Added :
                after.Presence == Presence.Absent ? ChangeKind.Deleted :
                before.FileId != after.FileId && before.ContentHash == after.ContentHash
                    ? ChangeKind.Unsupported : ChangeKind.Modified;
            result.Add(new(ChangeId(session.Id, kind, path, null), kind, path, null,
                before, after, "temporally_correlated",
                kind == ChangeKind.Unsupported ?
                    "unsupported_to_apply: file identity changed without a supported operation." : null));
        }
        return result;
    }

    private static string ChangeId(string sessionId, ChangeKind kind, string path, string? destination) =>
        ObjectStore.Hash(Encoding.UTF8.GetBytes($"{sessionId}|{kind}|{path}|{destination}"))[..24];

    private static string ScopeId(string key) => key.Contains('|') ? key[..key.IndexOf('|')] : "workspace";

    internal SessionRecord Report(string sessionId) => state.LoadSession(sessionId);

    internal string ReportJson(string sessionId)
    {
        var session = Report(sessionId);
        bool blocked = state.HasUnresolvedRuns() || state.HasUnresolvedPlans();
        return JsonSerializer.Serialize(new
        {
            schema_version = 1,
            session_id = session.Id,
            blast_status = session.Status,
            child_exit_code = session.ChildExitCode,
            child_process_id = session.ChildProcessId,
            final_scan_completed_utc = session.FinalScanCompletedUtc,
            background_processes = "untracked",
            cancel_requests = session.CancelRequests,
            console_control_mode = session.ConsoleControlMode,
            interruption_reason = session.InterruptionReason,
            child_launch_state = session.ChildLaunchState,
            store_modification_blocked = blocked,
            coverage = session.Coverage,
            coverage_detail = session.CoverageDetail,
            rule_version = session.RuleVersion,
            event_history = "unavailable",
            changes = session.Changes?.Select(change => new
            {
                change_id = change.Id,
                kind = change.Kind.ToString().ToLowerInvariant(),
                path = change.Path,
                scope_id = ScopeId(change.Path),
                scope_kind = session.Scopes.Single(s => s.Id == ScopeId(change.Path)).Kind,
                destination = change.Destination,
                attribution = change.Attribution,
                baseline_presence = change.Baseline.Presence.ToString().ToLowerInvariant(),
                final_presence = change.Final.Presence.ToString().ToLowerInvariant(),
                operation_supported = change.Kind != ChangeKind.Unsupported,
                snapshot_integrity = SnapshotIntegrity(change),
                apply_eligibility = blocked ? "blocked_unresolved_store_state" : "unverified",
                unsupported_reason = change.UnsupportedReason
            })
        });
    }

    private string SnapshotIntegrity(ChangeRecord change)
    {
        try
        {
            VerifyObjectIfPresent(change.Baseline);
            VerifyObjectIfPresent(change.Final);
            return "verified";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   InvalidOperationException or CryptographicException)
        {
            return "missing_or_corrupt";
        }
    }

    internal string ReportText(string sessionId)
    {
        var session = Report(sessionId);
        bool blocked = state.HasUnresolvedRuns() || state.HasUnresolvedPlans();
        var builder = new StringBuilder();
        builder.AppendLine($"Session {session.Id}: {session.Status}; coverage={session.Coverage}; child exit={session.ChildExitCode}");
        if (blocked) builder.AppendLine("Run and apply blocked: unresolved store state.");
        builder.AppendLine("Event history: unavailable; changes are final-state differences only. " +
            "Background processes are not tracked after the direct child exits.");
        foreach (var change in session.Changes ?? [])
            builder.AppendLine($"{change.Id} {change.Kind} {EscapeControl(change.Path)} [{change.Attribution}]");
        return builder.ToString();
    }

    private static string EscapeControl(string value) =>
        string.Concat(value.Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()));

    internal RestorePlan Preview(string sessionId, IEnumerable<string> selectedIds)
    {
        using var storeLock = state.AcquireLock();
        state.AuditUnresolved();
        var session = state.LoadSession(sessionId);
        if (session.Status != "complete" || session.Changes is null)
            throw new InvalidOperationException("Session has no complete final scan.");
        ValidateHistoricalDirectories(session);
        var ids = selectedIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (string id in ids)
        {
            var change = session.Changes.SingleOrDefault(c => c.Id == id)
                ?? throw new ArgumentException("Unknown change ID: " + id);
            if (change.Kind == ChangeKind.Unsupported) throw new NotSupportedException(change.UnsupportedReason);
        }
        foreach (var group in session.Changes.Where(c => c.GroupId is not null).GroupBy(c => c.GroupId))
        {
            int selected = group.Count(c => ids.Contains(c.Id));
            if (selected > 0 && selected != group.Count())
                throw new InvalidOperationException("Associated change group must be selected in full: " + group.Key);
        }
        string planId = Guid.NewGuid().ToString("N");
        string payload = BuildExecutionPayload(session, planId, ids);
        string hash = ObjectStore.Hash(Encoding.UTF8.GetBytes(payload));
        var plan = new RestorePlan
        {
            Id = planId, SessionId = sessionId, Hash = hash, ExecutionPayload = payload,
            ChangeIds = ids, Status = ids.Count == 0 ? "no_operations" : "planned",
            Operations = ids.Select(id =>
            {
                var change = session.Changes.Single(c => c.Id == id);
                return new RestoreOperation
                {
                    ChangeId = id, Status = "planned",
                    ExpectedPrePresence = change.Final.Presence,
                    ExpectedPreParentVolume = change.Final.ParentVolume,
                    ExpectedPreParentId = change.Final.ParentId,
                    ExpectedPostPresence = change.Baseline.Presence,
                    ExpectedPostHash = change.Baseline.ContentHash,
                    ExpectedPostLength = change.Baseline.Length
                };
            }).ToList()
        };
        state.SavePlan(plan);
        BoundaryForTest?.Invoke("plan_durable");
        return plan;
    }

    private static string BuildExecutionPayload(SessionRecord session, string planId, List<string> ids)
    {
        var payload = new PlanExecutionPayload(
            1, planId, session.Id, session.RuleVersion, session.Root,
            session.Scopes.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
            new SortedDictionary<string, FileIdentity>(session.BaselineDirectories, StringComparer.Ordinal),
            ids.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            ids.OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => session.Changes!.Single(c => c.Id == id)).ToList());
        return JsonSerializer.Serialize(payload);
    }

    internal static PlanExecutionPayload ValidateExecutionPayload(RestorePlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.ExecutionPayload))
            throw new InvalidDataException("Plan has no immutable execution payload.");
        string computed = ObjectStore.Hash(Encoding.UTF8.GetBytes(plan.ExecutionPayload));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(computed), Convert.FromHexString(plan.Hash)))
            throw new InvalidDataException("Plan payload hash mismatch.");
        var payload = JsonSerializer.Deserialize<PlanExecutionPayload>(plan.ExecutionPayload)
            ?? throw new InvalidDataException("Invalid plan payload.");
        if (payload.SchemaVersion != 1 || payload.PlanId != plan.Id ||
            payload.SessionId != plan.SessionId ||
            !payload.SelectedChangeIds.SequenceEqual(plan.ChangeIds) ||
            payload.Changes.Count != plan.ChangeIds.Count ||
            plan.Operations.Count != plan.ChangeIds.Count)
            throw new InvalidDataException("Plan identity or selection differs from execution payload.");
        foreach (var change in payload.Changes)
        {
            var op = plan.Operations.SingleOrDefault(o => o.ChangeId == change.Id)
                ?? throw new InvalidDataException("Plan operation is missing.");
            if (op.ExpectedPrePresence != change.Final.Presence ||
                op.ExpectedPreParentVolume != change.Final.ParentVolume ||
                op.ExpectedPreParentId != change.Final.ParentId ||
                op.ExpectedPostPresence != change.Baseline.Presence ||
                op.ExpectedPostHash != change.Baseline.ContentHash ||
                op.ExpectedPostLength != change.Baseline.Length)
                throw new InvalidDataException("Plan operation pre/post state differs from execution payload.");
        }
        return payload;
    }

    private static void EnsureSessionMatchesPayload(SessionRecord session, RestorePlan plan)
    {
        string rebuilt = BuildExecutionPayload(session, plan.Id, plan.ChangeIds);
        if (!rebuilt.Equals(plan.ExecutionPayload, StringComparison.Ordinal))
            throw new InvalidOperationException("Saved session changed after plan confirmation.");
    }

    internal ApplyResult Apply(string planId, string confirmedHash)
    {
        using var storeLock = state.AcquireLock();
        state.AuditUnresolved();
        if (state.HasUnresolvedRuns())
            throw new InvalidOperationException("Unresolved run blocks apply in this state store.");
        var plan = state.LoadPlan(planId);
        var execution = ValidateExecutionPayload(plan);
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(plan.Hash), Convert.FromHexString(confirmedHash)))
            throw new InvalidOperationException("Plan confirmation hash does not match.");
        var session = state.LoadSession(plan.SessionId);
        EnsureSessionMatchesPayload(session, plan);
        if (plan.ChangeIds.Count == 0) return new("no operations applied", 0, plan.Operations);
        if (plan.Status == "verified") return VerifyRepeated(plan, session, execution);
        if (plan.Status != "planned") throw new InvalidOperationException("Plan is not safe to apply: " + plan.Status);
        if (state.HasUnresolvedPlans())
            throw new InvalidOperationException("Unresolved restore intent blocks further apply in this state store.");
        if (session.Status != "complete") throw new InvalidOperationException("Session is incomplete.");
        var selected = execution.Changes;

        // Preflight every selected operation before beginning any group or destructive step.
        try
        {
            ValidateHistoricalDirectories(session);
            foreach (var change in selected) ValidatePreflight(session, change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or
                                   InvalidOperationException or NotSupportedException)
        {
            plan.Status = "conflict";
            foreach (var op in plan.Operations) { op.Status = "conflict"; op.Message = ex.Message; }
            state.SavePlan(plan);
            return new("no operations applied", 0, plan.Operations);
        }

        plan.Status = "applying";
        state.SavePlan(plan);
        int applied = 0;
        foreach (var change in selected)
        {
            var operation = plan.Operations.Single(o => o.ChangeId == change.Id);
            try
            {
                Prepare(session, change, plan, operation);
                Execute(session, change, plan, operation, execution);
                applied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
                                       NotSupportedException or System.ComponentModel.Win32Exception or
                                       CryptographicException)
            {
                bool mayHaveChangedTarget = operation.Status == "executing";
                operation.Status = "in_doubt";
                operation.Message = ex.GetType().Name + ": " + ex.Message;
                plan.Status = "in_doubt";
                state.SavePlan(plan);
                return new(mayHaveChangedTarget ? "in doubt; target may have changed" :
                    applied == 0 ? "no operations applied; in doubt" : "partial; in doubt", applied,
                    plan.Operations);
            }
        }
        plan.Status = "verified";
        state.SavePlan(plan);
        return new("verified", applied, plan.Operations);
    }

    private ApplyResult VerifyRepeated(RestorePlan plan, SessionRecord session,
        PlanExecutionPayload execution)
    {
        ValidateHistoricalDirectories(session);
        foreach (var op in plan.Operations)
        {
            var change = execution.Changes.Single(c => c.Id == op.ChangeId);
            var expected = Expected(change);
            var actual = Capture(session, change.Path).State;
            if (!LogicalMatches(actual, expected) ||
                actual.ParentId != op.ActualParentId || actual.ParentVolume != op.ActualParentVolume ||
                (actual.Presence == Presence.Present &&
                 (actual.FileId != op.ActualFileId || actual.Volume != op.ActualVolume)))
                throw new InvalidOperationException("Previously restored path changed.");
            if (change.Kind == ChangeKind.Renamed &&
                Capture(session, change.Destination!).State.Presence != Presence.Absent)
                throw new InvalidOperationException("Previously restored rename destination changed.");
        }
        return new("no operations applied", 0, plan.Operations);
    }

    private void ValidatePreflight(SessionRecord session, ChangeRecord change)
    {
        if (change.Kind == ChangeKind.Unsupported) throw new NotSupportedException(change.UnsupportedReason);
        VerifyObjectIfPresent(change.Baseline);
        VerifyObjectIfPresent(change.Final);
        if (change.Kind == ChangeKind.Deleted)
        {
            FileMetadata.RequireCreatableSecurity(change.Baseline.Security!);
            if (change.Baseline.Attributes != FileAttributes.Archive)
                throw new NotSupportedException("Deleted file attributes cannot be recreated before content.");
        }
        if ((change.Kind is ChangeKind.Modified or ChangeKind.Renamed) &&
            !change.Baseline.Attributes.HasFlag(FileAttributes.Archive))
            throw new NotSupportedException("File attributes cannot be preserved by this restore operation.");
        string currentPath = change.Kind == ChangeKind.Renamed ? change.Destination! : change.Path;
        var current = Capture(session, currentPath).State;
        if (!PreMatches(current, change.Final)) throw new InvalidOperationException("Current file conflicts with A.");
        if (change.Kind == ChangeKind.Renamed)
        {
            var source = Capture(session, change.Path).State;
            if (source.Presence != Presence.Absent ||
                source.ParentId != change.Baseline.ParentId)
                throw new InvalidOperationException("Rename source is occupied or parent changed.");
        }
    }

    private void VerifyObjectIfPresent(FileState file)
    {
        if (file.Presence == Presence.Unknown) throw new InvalidOperationException("Unknown state cannot be restored.");
        if (file.Presence == Presence.Present)
        {
            if (file.Security is null)
                throw new InvalidDataException("Present state lacks versioned owner/DACL metadata.");
            FileMetadata.RequireVersion(file.Security);
            FileMetadata.CheckAttributes(file.Attributes);
            if (file.ObjectId is null) throw new InvalidDataException("Present state has no content object.");
            objects.Verify(file.ObjectId, file.Length);
        }
    }

    private static bool PreMatches(FileState current, FileState expected)
    {
        if (current.Presence == Presence.Unknown || current.Presence != expected.Presence ||
            current.ParentId != expected.ParentId || current.ParentVolume != expected.ParentVolume)
            return false;
        return current.Presence == Presence.Absent ||
            current.Volume == expected.Volume && current.FileId == expected.FileId &&
            current.Length == expected.Length && current.ContentHash == expected.ContentHash &&
            current.Links == expected.Links && current.Attributes == expected.Attributes &&
            current.Security == expected.Security;
    }

    private static bool LogicalMatches(FileState actual, FileState expected) =>
        actual.Presence != Presence.Unknown && actual.Presence == expected.Presence &&
        (actual.Presence == Presence.Absent ||
         actual.ContentHash == expected.ContentHash && actual.Length == expected.Length &&
         actual.Attributes == expected.Attributes && actual.Security == expected.Security);

    private static FileState Expected(ChangeRecord change) => change.Baseline;

    private void Prepare(SessionRecord session, ChangeRecord change, RestorePlan plan, RestoreOperation op)
    {
        string currentPath = change.Kind == ChangeKind.Renamed ? change.Destination! : change.Path;
        var current = Capture(session, currentPath);
        if (!PreMatches(current.State, change.Final)) throw new InvalidOperationException("Current state changed before safety copy.");
        if (current.State.Presence == Presence.Present)
        {
            string safety = objects.Save(current.Bytes!);
            BoundaryForTest?.Invoke("safety_object_published");
            op.SafetyObjectId = safety;
        }
        op.Status = "safety_copied";
        state.SavePlan(plan);
        BoundaryForTest?.Invoke("safety_copied");
        if (op.SafetyObjectId is not null) objects.Verify(op.SafetyObjectId, current.State.Length);
        op.Status = "intent_durable";
        state.SavePlan(plan);
        BoundaryForTest?.Invoke("intent_durable");
    }

    private void Execute(SessionRecord session, ChangeRecord change, RestorePlan plan, RestoreOperation op,
        PlanExecutionPayload execution)
    {
        op.Status = "executing";
        state.SavePlan(plan);
        if (op.SafetyObjectId is not null) objects.Verify(op.SafetyObjectId, change.Final.Length);
        string currentKey = change.Kind == ChangeKind.Renamed ? change.Destination! : change.Path;
        var (currentScope, currentRelative) = Resolve(session, currentKey);
        string path = FileSystemScope.FullPath(currentScope.Root, currentRelative);
        using var parents = new PathGuard(currentScope.Root, path, currentScope.Id,
            execution.BaselineDirectories);
        if (parents.ParentIdentity.Volume != change.Final.ParentVolume ||
            parents.ParentIdentity.Index != change.Final.ParentId)
            throw new InvalidOperationException("Parent identity changed.");
        switch (change.Kind)
        {
            case ChangeKind.Modified:
            {
                using var handle = WindowsFiles.OpenFile(path, write: true);
                ValidateExecutableLeaf(handle, path, change.Final);
                using var stream = new FileStream(handle, FileAccess.ReadWrite);
                byte[] current = ReadBounded(stream);
                if (ObjectStore.Hash(current) != change.Final.ContentHash)
                    throw new InvalidOperationException("File content changed.");
                byte[] target = objects.Read(change.Baseline.ObjectId!);
                parents.Check();
                ValidateExecutableLeaf(handle, path, change.Final);
                stream.Position = 0;
                stream.SetLength(0);
                stream.Write(target);
                stream.Flush(true);
                BoundaryForTest?.Invoke("target_modified");
                var actual = CaptureLive(stream, handle, change.Path, path, parents);
                FinishVerification(change, plan, op, actual);
                return;
            }
            case ChangeKind.Deleted:
            {
                if (File.Exists(path) || Directory.Exists(path))
                    throw new InvalidOperationException("Deleted path became occupied.");
                byte[] target = objects.Read(change.Baseline.ObjectId!);
                parents.Check();
                if (change.Baseline.Attributes != FileAttributes.Archive)
                    throw new NotSupportedException("Deleted file attributes cannot be created before content safely.");
                BoundaryForTest?.Invoke("before_create_new");
                using var stream = FileMetadata.CreateWithSecurity(path, change.Baseline.Security!);
                var created = WindowsFiles.ValidateSupportedLeaf(stream.SafeFileHandle, path);
                if (FileMetadata.Read(stream) != change.Baseline.Security ||
                    created.Attributes != change.Baseline.Attributes)
                    throw new IOException("Created file metadata differs before content write.");
                stream.Write(target);
                stream.Flush(true);
                BoundaryForTest?.Invoke("target_modified");
                var actual = CaptureLive(stream, stream.SafeFileHandle, change.Path, path, parents);
                if (actual.Volume != created.Volume || actual.FileId != created.Index)
                    throw new IOException("Created restore object changed before verification.");
                FinishVerification(change, plan, op, actual);
                return;
            }
            case ChangeKind.Added:
            {
                using var handle = WindowsFiles.OpenFile(path, write: false, delete: true);
                ValidateExecutableLeaf(handle, path, change.Final);
                if (ObjectStore.Hash(ReadHandleWithoutClosing(handle)) != change.Final.ContentHash)
                    throw new InvalidOperationException("File content changed.");
                parents.Check();
                ValidateExecutableLeaf(handle, path, change.Final);
                WindowsFiles.DeleteByHandle(handle);
                break;
            }
            case ChangeKind.Renamed:
            {
                var (sourceScope, sourceRelative) = Resolve(session, change.Path);
                if (sourceScope.Id != currentScope.Id)
                    throw new NotSupportedException("Cross-scope rename is unsupported.");
                string source = FileSystemScope.FullPath(sourceScope.Root, sourceRelative);
                if (File.Exists(source) || Directory.Exists(source))
                    throw new InvalidOperationException("Rename source became occupied.");
                using var handle = WindowsFiles.OpenFile(path, write: false, delete: true);
                ValidateExecutableLeaf(handle, path, change.Final);
                if (ObjectStore.Hash(ReadHandleWithoutClosing(handle)) != change.Final.ContentHash)
                    throw new InvalidOperationException("Renamed file content changed.");
                parents.Check();
                ValidateExecutableLeaf(handle, path, change.Final);
                BoundaryForTest?.Invoke("before_rename_by_handle");
                WindowsFiles.RenameByHandle(handle, source);
                BoundaryForTest?.Invoke("target_modified");
                using (var stream = new FileStream(handle, FileAccess.Read))
                {
                    var actual = CaptureLive(stream, handle, change.Path, source, parents);
                    if (FileSystemScope.CaptureWithGuard(change.Destination!, path, parents).State.Presence != Presence.Absent)
                        throw new IOException("Rename destination still exists.");
                    FinishVerification(change, plan, op, actual);
                }
                return;
            }
            default: throw new NotSupportedException("Unsupported change kind.");
        }
        BoundaryForTest?.Invoke("target_modified");
        var (verifyScope, verifyRelative) = Resolve(session, change.Path);
        var captured = FileSystemScope.CaptureWithGuard(change.Path,
            FileSystemScope.FullPath(verifyScope.Root, verifyRelative), parents).State;
        FinishVerification(change, plan, op, captured);
    }

    private static FileState CaptureLive(FileStream stream, Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string relative, string expectedPath, PathGuard parents)
    {
        parents.Check();
        var before = WindowsFiles.ValidateSupportedLeaf(handle, expectedPath);
        byte[] bytes = ReadBounded(stream);
        var identity = WindowsFiles.ValidateSupportedLeaf(handle, expectedPath);
        if (identity != before) throw new IOException("Restore object changed during verification.");
        parents.Check();
        return new FileState(Presence.Present, relative, ObjectStore.Hash(bytes), null,
            bytes.Length, identity.Volume, identity.Index, identity.Links, identity.Attributes,
            parents.ParentIdentity.Volume, parents.ParentIdentity.Index,
            Security: FileMetadata.Read(stream));
    }

    private static void ValidateExecutableLeaf(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string path, FileState expected)
    {
        var identity = WindowsFiles.ValidateSupportedLeaf(handle, path);
        if (identity.Volume != expected.Volume || identity.Index != expected.FileId ||
            identity.Links != expected.Links || identity.Attributes != expected.Attributes ||
            FileMetadata.Read(handle) != expected.Security)
            throw new InvalidOperationException("Final opened file identity or attributes differ from A.");
    }

    private void FinishVerification(ChangeRecord change, RestorePlan plan, RestoreOperation op, FileState actual)
    {
        if (!LogicalMatches(actual, Expected(change)))
            throw new IOException("Post-operation logical verification failed.");
        op.ActualVolume = actual.Presence == Presence.Present ? actual.Volume : null;
        op.ActualFileId = actual.Presence == Presence.Present ? actual.FileId : null;
        op.ActualParentVolume = actual.ParentVolume;
        op.ActualParentId = actual.ParentId;
        op.ActualPresence = actual.Presence;
        op.ActualContentHash = actual.ContentHash;
        op.Status = "verified";
        state.SavePlan(plan);
        BoundaryForTest?.Invoke("verified");
    }

    private static byte[] ReadBounded(FileStream stream)
    {
        if (stream.Length > FileSystemScope.MaxFileBytes) throw new NotSupportedException("File is too large.");
        stream.Position = 0;
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.Length != bytes.Length) throw new IOException("File length changed during verification.");
        stream.Position = 0;
        byte[] secondRead = new byte[bytes.Length];
        stream.ReadExactly(secondRead);
        if (!bytes.AsSpan().SequenceEqual(secondRead) || stream.Length != bytes.Length)
            throw new IOException("File content changed during verification.");
        return bytes;
    }

    private static byte[] ReadHandleWithoutClosing(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        // The borrowed wrapper is disposed by FileStream; the original exclusive handle stays open.
        using var borrowed = new Microsoft.Win32.SafeHandles.SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowed, FileAccess.Read);
        return ReadBounded(stream);
    }
}
