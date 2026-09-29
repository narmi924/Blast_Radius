using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Blast;

internal sealed class StateStore
{
    internal static Action<SessionRecord>? BeforeSessionSaveForTest { get; set; }
    private readonly string directory;
    private readonly string database;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal StateStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        StorageAccess.EnsurePrivateDirectory(this.directory);
        database = Path.Combine(this.directory, "state.db");
        using var storeLock = AcquireLock();
        using var connection = Open();
        Command(connection, """
            CREATE TABLE IF NOT EXISTS sessions(
              id TEXT PRIMARY KEY, status TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS plans(
              id TEXT PRIMARY KEY, session_id TEXT NOT NULL, hash TEXT NOT NULL,
              status TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS operations(
              plan_id TEXT NOT NULL, change_id TEXT NOT NULL, status TEXT NOT NULL,
              safety_object TEXT, payload TEXT NOT NULL,
              PRIMARY KEY(plan_id, change_id));
            """).ExecuteNonQuery();
    }

    internal FileStream AcquireLock()
    {
        try
        {
            string path = Path.Combine(directory, "state.lock");
            var stream = new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            try { StorageAccess.VerifyFile(path); return stream; }
            catch { stream.Dispose(); throw; }
        }
        catch (IOException exception)
        {
            throw new StoreBusyException("State store is busy.", exception);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        StorageAccess.VerifyFile(database);
        Command(connection, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;")
            .ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    internal void SaveSession(SessionRecord session)
    {
        BeforeSessionSaveForTest?.Invoke(session);
        using var connection = Open();
        using var command = Command(connection, """
            INSERT INTO sessions(id,status,payload) VALUES($id,$status,$payload)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,payload=excluded.payload;
            """);
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$status", session.Status);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session, JsonOptions));
        command.ExecuteNonQuery();
    }

    internal SessionRecord LoadSession(string id)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT payload FROM sessions WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id);
        string payload = (string?)command.ExecuteScalar() ?? throw new KeyNotFoundException("Session not found.");
        return JsonSerializer.Deserialize<SessionRecord>(payload, JsonOptions)
            ?? throw new InvalidDataException("Invalid session record.");
    }

    internal void SavePlan(RestorePlan plan)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = Command(connection, """
            INSERT INTO plans(id,session_id,hash,status,payload) VALUES($id,$session,$hash,$status,$payload)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,payload=excluded.payload;
            """, transaction))
        {
            command.Parameters.AddWithValue("$id", plan.Id);
            command.Parameters.AddWithValue("$session", plan.SessionId);
            command.Parameters.AddWithValue("$hash", plan.Hash);
            command.Parameters.AddWithValue("$status", plan.Status);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(plan, JsonOptions));
            command.ExecuteNonQuery();
        }
        foreach (var operation in plan.Operations)
        {
            using var command = Command(connection, """
                INSERT INTO operations(plan_id,change_id,status,safety_object,payload)
                VALUES($plan,$change,$status,$safety,$payload)
                ON CONFLICT(plan_id,change_id) DO UPDATE SET
                  status=excluded.status,safety_object=excluded.safety_object,payload=excluded.payload;
                """, transaction);
            command.Parameters.AddWithValue("$plan", plan.Id);
            command.Parameters.AddWithValue("$change", operation.ChangeId);
            command.Parameters.AddWithValue("$status", operation.Status);
            command.Parameters.AddWithValue("$safety", (object?)operation.SafetyObjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(operation, JsonOptions));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal RestorePlan LoadPlan(string id)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT payload FROM plans WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id);
        string payload = (string?)command.ExecuteScalar() ?? throw new KeyNotFoundException("Plan not found.");
        return JsonSerializer.Deserialize<RestorePlan>(payload, JsonOptions)
            ?? throw new InvalidDataException("Invalid restore plan.");
    }

    // An audited uncertain operation may have changed any of its paths. Until a
    // reconciliation workflow exists, the whole store is conservatively blocked.
    internal bool HasUnresolvedPlans()
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT EXISTS(SELECT 1 FROM plans WHERE status='in_doubt');");
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    // A PID may have been reused, and a failed process query says nothing about the
    // original child. Only a durable, successful completion clears this barrier.
    internal bool HasUnresolvedRuns()
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT payload FROM sessions WHERE status<>'complete';");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var session = JsonSerializer.Deserialize<SessionRecord>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidDataException("Invalid session record during run audit.");
            if (session.ChildLaunchState == "not_started") continue;
            if (session.ChildLaunchState is "possible" or "exited") return true;
            // Older records lack ChildLaunchState. A historical ready/running window
            // cannot prove that Process.Start was never reached.
            if (session.ChildProcessId is not null || session.ChildExitCode is not null ||
                session.Status is "ready" or "launch_pending" or "running" or "finalizing" ||
                session.InterruptionReason is "unfinished_on_restart_from_ready" or
                    "unfinished_on_restart_from_running" or "unfinished_on_restart_from_finalizing")
                return true;
        }
        return false;
    }

    internal bool HasStoredRecords()
    {
        using var connection = Open();
        using var command = Command(connection,
            "SELECT EXISTS(SELECT 1 FROM sessions UNION ALL SELECT 1 FROM plans);");
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    internal IReadOnlyList<RestorePlan> PlansForSession(string sessionId)
    {
        var ids = new List<string>();
        using (var connection = Open())
        using (var command = Command(connection, "SELECT id FROM plans WHERE session_id=$session;"))
        {
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return ids.Select(LoadPlan).ToList();
    }

    internal IReadOnlyList<string> SessionIdsForTest()
    {
        var ids = new List<string>();
        using var connection = Open();
        using var command = Command(connection, "SELECT id FROM sessions;");
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    internal ObjectAudit InspectObjects()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sessionId in SessionIdsForTest())
        {
            var session = LoadSession(sessionId);
            foreach (var file in session.Baseline.Values.Concat(
                         session.Final is null ? Enumerable.Empty<FileState>() : session.Final.Values))
                if (file.ObjectId is not null) referenced.Add(file.ObjectId);
            foreach (var plan in PlansForSession(sessionId))
            {
                foreach (var operation in plan.Operations)
                    if (operation.SafetyObjectId is not null) referenced.Add(operation.SafetyObjectId);
                var execution = SessionEngine.ValidateExecutionPayload(plan);
                foreach (var change in execution.Changes)
                {
                    if (change.Baseline.ObjectId is not null) referenced.Add(change.Baseline.ObjectId);
                    if (change.Final.ObjectId is not null) referenced.Add(change.Final.ObjectId);
                }
            }
        }
        string objectDirectory = Path.Combine(directory, "objects");
        var pending = Directory.GetFiles(objectDirectory, ".pending-*")
            .Select(path => Path.GetFileName(path)!).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var unreferenced = Directory.GetFiles(objectDirectory, "*.bro")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(id => !referenced.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        return new(pending, unreferenced);
    }

    internal void AuditUnresolved()
    {
        List<string> planIds = [];
        using (var connection = Open())
        using (var command = Command(connection,
            "SELECT id FROM plans WHERE status='applying';"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) planIds.Add(reader.GetString(0));

        foreach (string id in planIds)
        {
            var plan = LoadPlan(id);
            PlanExecutionPayload execution;
            try { execution = SessionEngine.ValidateExecutionPayload(plan); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException)
            {
                plan.Status = "in_doubt";
                foreach (var operation in plan.Operations)
                {
                    operation.Status = "in_doubt";
                    operation.Message = "Interrupted plan payload invalid: " + ex.GetType().Name;
                }
                SavePlan(plan);
                continue;
            }
            var session = new SessionRecord
            {
                Id = execution.SessionId, Root = execution.Root, Scopes = execution.Scopes,
                BaselineDirectories = new(execution.BaselineDirectories, StringComparer.OrdinalIgnoreCase),
                Baseline = [], Changes = execution.Changes, Status = "complete"
            };
            var objects = new ObjectStore(directory, allowNewKey: false);
            foreach (var operation in plan.Operations.Where(o => o.Status is "intent_durable" or "executing"))
            {
                var change = session.Changes!.Single(c => c.Id == operation.ChangeId);
                var observed = SessionEngine.Capture(session, change.Path).State;
                operation.ActualPresence = observed.Presence;
                operation.ActualContentHash = observed.ContentHash;
                operation.ActualVolume = observed.Presence == Presence.Present ? observed.Volume : null;
                operation.ActualFileId = observed.Presence == Presence.Present ? observed.FileId : null;
                operation.ActualParentVolume = observed.ParentVolume;
                operation.ActualParentId = observed.ParentId;
                string safety = CheckSafety(objects, operation, change.Final.Length);
                operation.Status = "in_doubt";
                operation.Message = $"Interrupted after durable intent; observed target={observed.Presence}, " +
                    $"hash={observed.ContentHash ?? "none"}, safety={safety}. No automatic retry.";
            }
            foreach (var operation in plan.Operations.Where(o => o.Status is "planned" or "safety_copied"))
            {
                var change = session.Changes!.Single(c => c.Id == operation.ChangeId);
                var currentPath = change.Kind == ChangeKind.Renamed ? change.Destination! : change.Path;
                var observed = SessionEngine.Capture(session, currentPath).State;
                bool unchanged = observed.Presence == change.Final.Presence &&
                    observed.ParentId == change.Final.ParentId &&
                    (observed.Presence == Presence.Absent ||
                     observed.FileId == change.Final.FileId && observed.ContentHash == change.Final.ContentHash);
                string safety = CheckSafety(objects, operation, change.Final.Length);
                operation.Status = unchanged ? "interrupted_before_intent" : "in_doubt";
                operation.Message = $"No durable mutation intent; observed prestate={(unchanged ? "unchanged" : "changed")}, " +
                    $"safety={safety}. No automatic replay.";
            }
            plan.Status = "in_doubt";
            SavePlan(plan);
        }

        List<string> sessionIds = [];
        using (var connection = Open())
        using (var command = Command(connection,
            "SELECT id FROM sessions WHERE status IN ('baselining','ready','launch_pending','running','finalizing');"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) sessionIds.Add(reader.GetString(0));
        foreach (string id in sessionIds)
        {
            var session = LoadSession(id);
            string previous = session.Status;
            session.Status = "interrupted";
            session.Coverage = "incomplete";
            session.InterruptionReason = "unfinished_on_restart_from_" + previous;
            session.CoverageDetail.ScanFailure = "No durable session completion was recorded; " +
                "a direct child or background writer may still be running.";
            SaveSession(session);
        }
    }

    private static string CheckSafety(ObjectStore objects, RestoreOperation operation, long expectedLength)
    {
        if (operation.SafetyObjectId is null)
            return operation.ExpectedPrePresence == Presence.Absent ? "not_required_for_absent" : "missing";
        try { objects.Verify(operation.SafetyObjectId, expectedLength); return "verified"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        { return "invalid:" + ex.GetType().Name; }
    }

    internal string DatabasePathForTest => database;
}

internal sealed class StoreBusyException(string message, Exception inner) : IOException(message, inner);
