using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Blast;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && args[0] == "mutate") return Mutate(args);
if (args.Length > 0 && args[0] == "mutate-nested") return MutateNested(args);
if (args.Length > 0 && args[0] == "mutate-relative") return MutateRelative(args);
if (args.Length > 0 && args[0] == "hold-lock") return HoldLock(args);
if (args.Length > 0 && args[0] == "apply-worker") return ApplyWorker(args);
if (args.Length > 0 && args[0] == "preview-worker") return PreviewWorker(args);
if (args.Length > 0 && args[0] == "object-save-worker") return ObjectSaveWorker(args);
if (args.Length > 0 && args[0] == "stdio-child") return StdioChild(args);
if (args.Length > 0 && args[0] == "stdio-wrapper") return StdioWrapper(args);
if (args.Length > 0 && args[0] == "ctrlc-child") return CtrlCChild(args);
if (args.Length > 0 && args[0] == "ctrlc-default-child") return CtrlCDefaultChild(args);
if (args.Length > 0 && args[0] == "ctrlc-wrapper") return CtrlCWrapper(args);
if (args.Length > 0 && args[0] == "ctrlc-terminal-harness") return CtrlCTerminalHarness(args);
if (args.Length > 0 && args[0] == "ctrlc-terminal-supervisor") return CtrlCTerminalSupervisor(args);
if (args.Length > 0 && args[0] == "sqlite-failure-wrapper") return SqliteFailureWrapper(args);
if (args.Length > 0 && args[0] == "run-worker") return RunWorker(args);
if (args.Length > 0 && args[0] == "failure-wrapper") return FailureWrapper(args);
if (args.Length > 0 && args[0] == "failfast-child") Environment.FailFast("Synthetic child failure.");
if (args.Length > 0 && args[0] == "probe") return Probe();
if (args.Length > 0 && args[0] == "demo") return Demo();
if (args.Length == 0 || args[0] == "all") return All();
if (args.Length > 1 && args[0] == "test") return All(args[1]);
Console.Error.WriteLine("Use: Blast.Tests all|probe|demo");
return 2;

static int Mutate(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    string a = Path.Combine(fixture.Root, "alpha.txt");
    switch (args[3])
    {
        case "modify": File.WriteAllText(a, "after"); break;
        case "add": File.WriteAllText(Path.Combine(fixture.Root, "new.txt"), "new content"); break;
        case "add-empty": File.WriteAllBytes(Path.Combine(fixture.Root, "new.txt"), []); break;
        case "delete": File.Delete(a); break;
        case "rename": File.Move(a, Path.Combine(fixture.Root, "beta.txt")); break;
        case "double-modify":
            File.WriteAllText(a, "after");
            File.WriteAllText(Path.Combine(fixture.Root, "bravo.txt"), "after bravo");
            break;
        case "swap":
            string b = Path.Combine(fixture.Root, "bravo.txt");
            string intermediate = Path.Combine(fixture.Root, "intermediate.tmp");
            File.Move(a, intermediate);
            File.Move(b, a);
            File.Move(intermediate, b);
            break;
        case "overwrite-rename":
            File.Move(a, Path.Combine(fixture.Root, "bravo.txt"), overwrite: true);
            break;
        case "cycle-three":
            string second = Path.Combine(fixture.Root, "bravo.txt");
            string third = Path.Combine(fixture.Root, "charlie.txt");
            string temporary = Path.Combine(fixture.Root, "cycle.tmp");
            File.Move(a, temporary);
            File.Move(second, a);
            File.Move(third, second);
            File.Move(temporary, third);
            break;
        case "cross-parent-rename":
            File.Move(Path.Combine(fixture.Root, "nested", "item.txt"),
                Path.Combine(fixture.Root, "other", "item.txt"));
            break;
        case "move-to-excluded":
            File.Move(a, Path.Combine(fixture.Root, ".env"));
            break;
        case "token-after": File.WriteAllText(a, args[4]); break;
        case "git-mutate":
            File.WriteAllText(a, "agent edit");
            File.WriteAllText(Path.Combine(fixture.Root, "untracked.txt"), "agent untracked");
            Git(fixture.Root, "add", "alpha.txt");
            break;
        case "delete-parent":
            Directory.Delete(Path.Combine(fixture.Root, "nested"), recursive: true);
            break;
        case "mark":
            File.WriteAllText(Path.Combine(fixture.StateDirectory, "child-started.signal"), "started");
            break;
        case "mark-next":
            File.WriteAllText(Path.Combine(fixture.StateDirectory, "next-child-started.signal"), "started");
            break;
        case "exit-23":
            File.WriteAllText(Path.Combine(fixture.StateDirectory, "child-started.signal"), "started");
            return 23;
        case "hang":
            File.WriteAllText(Path.Combine(fixture.StateDirectory, "child-started.signal"),
                Environment.ProcessId.ToString());
            Directory.SetCurrentDirectory(fixture.StateDirectory);
            Thread.Sleep(TimeSpan.FromSeconds(30));
            break;
        case "external-modify-add":
            File.WriteAllText(Path.Combine(fixture.DirectoryPath, "outside-file", "chosen.txt"), "after external");
            File.WriteAllText(Path.Combine(fixture.DirectoryPath, "outside-dir", "new.txt"), "new external");
            File.WriteAllText(Path.Combine(fixture.DirectoryPath, "outside-file", "neighbor.txt"), "neighbor after");
            break;
        case "external-delete":
            File.Delete(Path.Combine(fixture.DirectoryPath, "outside-file", "chosen.txt"));
            break;
        case "external-same-name":
            File.WriteAllText(a, "after workspace");
            File.WriteAllText(Path.Combine(fixture.DirectoryPath, "outside-dir", "alpha.txt"), "after external");
            break;
        case "binary-modify":
            File.WriteAllBytes(a, [0, 255, 1, 254, 2, 253]);
            break;
        default: throw new ArgumentException("Unknown fixture mutation.");
    }
    return 0;
}

static int MutateNested(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    File.WriteAllText(Path.Combine(fixture.Root, "nested", "item.bin"), "after");
    return 0;
}

static int MutateRelative(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    string path = Path.Combine(fixture.Root, args[3]);
    switch (args.Length > 4 ? args[4] : "modify")
    {
        case "modify":
        case "add": File.WriteAllText(path, "after"); break;
        case "delete": File.Delete(path); break;
        case "rename": File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "renamed.txt")); break;
        default: throw new ArgumentException("Unknown relative fixture mutation.");
    }
    return 0;
}

static int HoldLock(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    var state = new StateStore(fixture.StateDirectory);
    using var held = state.AcquireLock();
    File.WriteAllText(Path.Combine(fixture.StateDirectory, "held.signal"), "held");
    Thread.Sleep(TimeSpan.FromSeconds(30));
    return 0;
}

static int RunWorker(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "child_started")
            File.WriteAllText(Path.Combine(fixture.StateDirectory, "wrapper-running.signal"), "running");
    };
    var result = engine.Run(Child(fixture, "hang"));
    return result.WrapperExitCode;
}

static int FailureWrapper(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "before_final_scan") throw new IOException("Synthetic wrapper final-scan failure.");
    };
    var result = engine.Run(Child(fixture, "exit-23"));
    Console.WriteLine($"FAILURE_WRAPPER child={result.ChildExitCode} blast={result.BlastStatus}");
    return result.WrapperExitCode;
}

static int SqliteFailureWrapper(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    int terminalSaveAttempts = 0;
    StateStore.BeforeSessionSaveForTest = session =>
    {
        if (session.Status is "complete" or "incomplete") terminalSaveAttempts++;
    };
    RunResult result;
    try { result = new SessionEngine(fixture).Run(Child(fixture, "exit-23")); }
    finally { StateStore.BeforeSessionSaveForTest = null; }
    Console.WriteLine($"SQLITE_FAILURE child={result.ChildExitCode?.ToString() ?? "null"} " +
        $"pid={result.ChildProcessId?.ToString() ?? "null"} blast={result.BlastStatus} " +
        $"diagnostic_persisted={result.DiagnosticPersisted} " +
        $"terminal_save_attempts={terminalSaveAttempts} error={result.Error}");
    return result.WrapperExitCode;
}

static int ApplyWorker(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    var engine = new SessionEngine(fixture);
    if (args[5].StartsWith("crash-", StringComparison.Ordinal))
        engine.BoundaryForTest = boundary =>
        {
            if (boundary == args[5]["crash-".Length..])
                Environment.FailFast("Synthetic process termination at " + boundary + ".");
        };
    if (args[5] == "wait")
        engine.BoundaryForTest = boundary =>
        {
            if (boundary != "safety_copied") return;
            File.WriteAllText(Path.Combine(fixture.StateDirectory, "held.signal"), "held");
            string release = Path.Combine(fixture.StateDirectory, "release.signal");
            while (!File.Exists(release)) Thread.Sleep(20);
        };
    var result = engine.Apply(args[3], args[4]);
    Console.WriteLine($"worker: {result.Status}; verified={result.OperationsApplied}");
    return result.Status == "verified" ? 0 : 1;
}

static int PreviewWorker(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "plan_durable") Environment.FailFast("Synthetic crash after plan commit.");
    };
    engine.Preview(args[3], [args[4]]);
    return 0;
}

static int ObjectSaveWorker(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    string stage = args[3];
    string payload = args[4];
    var store = new ObjectStore(fixture.StateDirectory);
    ObjectStore.PublicationBoundaryForTest = (boundary, _) =>
    {
        if (boundary != stage) return;
        File.WriteAllText(Path.Combine(fixture.StateDirectory, "object-branch.signal"), boundary);
        Environment.FailFast("Synthetic new-object termination at " + boundary + ".");
    };
    store.Save(Encoding.UTF8.GetBytes(payload));
    return 0;
}

static int StdioChild(string[] args)
{
    string input = Console.ReadLine() ?? "<eof>";
    string payload = args[1] + "|" + args[2] + "|" + input;
    Console.WriteLine("CHILD_OUTPUT:" + payload);
    File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "stdio-result.txt"), payload);
    return 17;
}

static int StdioWrapper(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    var engine = new SessionEngine(fixture);
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("stdio-child");
    command.ArgumentList.Add("space value");
    command.ArgumentList.Add("quoted \"value\"");
    var run = engine.Run(command);
    Console.WriteLine($"WRAPPER_STATUS:{run.BlastStatus}:{run.ChildExitCode}:{run.ConsoleControlMode}");
    return run.WrapperExitCode;
}

static int CtrlCChild(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        File.WriteAllText(Path.Combine(fixture.StateDirectory, "ctrlc-child-handled.signal"), "handled");
        Environment.Exit(130);
    };
    File.WriteAllText(Path.Combine(fixture.StateDirectory, "ctrlc-child-ready.signal"), "ready");
    Thread.Sleep(TimeSpan.FromSeconds(25));
    return 0;
}

static int CtrlCDefaultChild(string[] args)
{
    using var fixture = SyntheticFixture.AttachWorker(args[1], args[2]);
    File.WriteAllText(Path.Combine(fixture.StateDirectory, "ctrlc-child-ready.signal"), "ready");
    Thread.Sleep(TimeSpan.FromSeconds(25));
    return 0;
}

static int CtrlCWrapper(string[] args)
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var notifier = Task.Run(() =>
    {
        string ready = Path.Combine(fixture.StateDirectory, "ctrlc-child-ready.signal");
        for (int i = 0; i < 1000 && !File.Exists(ready); i++) Thread.Sleep(10);
        if (File.Exists(ready)) File.WriteAllText(args[2], "ready");
    });
    var engine = new SessionEngine(fixture);
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add(args.Length > 3 && args[3] == "default" ? "ctrlc-default-child" : "ctrlc-child");
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    var result = engine.Run(command);
    notifier.GetAwaiter().GetResult();
    bool childHandled = File.Exists(Path.Combine(fixture.StateDirectory, "ctrlc-child-handled.signal"));
    int handlersAfterFirst = RunCancellation.ActiveConsoleHandlersForTest;
    var firstSession = engine.Report(result.SessionId);
    var second = engine.Run(Child(fixture, "mark"));
    int handlersAfterSecond = RunCancellation.ActiveConsoleHandlersForTest;
    var record = new
    {
        result.BlastStatus, result.ChildExitCode, result.CancelRequests, result.ConsoleControlMode,
        SessionStatus = firstSession.Status,
        FinalScanCompleted = firstSession.FinalScanCompletedUtc is not null,
        ChildHandled = childHandled,
        HandlersAfterFirst = handlersAfterFirst,
        SecondBlastStatus = second.BlastStatus,
        SecondChildExitCode = second.ChildExitCode,
        HandlersAfterSecond = handlersAfterSecond
    };
    File.WriteAllText(args[1], JsonSerializer.Serialize(record));
    return result.WrapperExitCode;
}

static int CtrlCTerminalHarness(string[] args)
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.WriteLine("BLOCKED: interactive console is not attached.");
        return 3;
    }
    string resultPath = Path.GetFullPath(args[1]);
    bool defaultChild = args.Length > 2 && args[2] == "default";
    string wrapperRecord = resultPath + ".wrapper.json";
    string ready = resultPath + ".ready";
    int harnessSignals = 0;
    ConsoleCancelEventHandler keepHarnessAlive = (_, e) =>
    {
        e.Cancel = true;
        Interlocked.Increment(ref harnessSignals);
    };
    Console.CancelKeyPress += keepHarnessAlive;
    try
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("ctrlc-wrapper");
        start.ArgumentList.Add(wrapperRecord);
        start.ArgumentList.Add(ready);
        start.ArgumentList.Add(defaultChild ? "default" : "controlled");
        using var wrapper = Process.Start(start) ?? throw new IOException("Wrapper did not start.");
        var readyWait = Stopwatch.StartNew();
        while (!File.Exists(ready) && readyWait.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
        if (!File.Exists(ready)) throw new IOException("Ctrl+C child did not become ready.");
        Console.WriteLine("READY_FOR_EXTERNAL_CTRL_C");
        if (!wrapper.WaitForExit(30000)) throw new TimeoutException("Wrapper did not finish after terminal input.");
        using var report = JsonDocument.Parse(File.ReadAllText(wrapperRecord));
        var root = report.RootElement;
        int childCode = root.GetProperty("ChildExitCode").GetInt32();
        string blast = root.GetProperty("BlastStatus").GetString()!;
        int wrapperCode = wrapper.ExitCode;
        bool childHandled = root.GetProperty("ChildHandled").GetBoolean();
        bool finalScanCompleted = root.GetProperty("FinalScanCompleted").GetBoolean();
        int cancels = root.GetProperty("CancelRequests").GetInt32();
        bool handlersReleased = root.GetProperty("HandlersAfterFirst").GetInt32() == 0 &&
            root.GetProperty("HandlersAfterSecond").GetInt32() == 0;
        bool secondRun = root.GetProperty("SecondBlastStatus").GetString() == "ok" &&
            root.GetProperty("SecondChildExitCode").GetInt32() == 0;
        bool expectedChild = defaultChild ? childCode != 0 && !childHandled : childCode == 130 && childHandled;
        int testExit = blast == "ok" && expectedChild && wrapperCode == childCode &&
            finalScanCompleted && root.GetProperty("SessionStatus").GetString() == "complete" &&
            cancels >= 1 && handlersReleased && secondRun && harnessSignals >= 1 ? 0 : 1;
        string observed = $"CHILD_EXIT={childCode} WRAPPER_PROCESS_EXIT={wrapperCode} " +
            $"TEST_EXECUTOR_EXIT={testExit} BLAST_STATUS={blast} CANCEL_REQUESTS={cancels} " +
            $"CHILD_MODE={(defaultChild ? "default" : "controlled")} CHILD_HANDLED={childHandled} " +
            $"FINAL_SCAN_COMPLETED={finalScanCompleted} " +
            $"HANDLERS_RELEASED={handlersReleased} SECOND_RUN_OK={secondRun} " +
            $"HARNESS_SIGNALS={harnessSignals}";
        Console.WriteLine(observed);
        File.WriteAllText(resultPath, "READY_FOR_EXTERNAL_CTRL_C\n" + observed + "\n");
        return testExit;
    }
    finally
    {
        Console.CancelKeyPress -= keepHarnessAlive;
        if (File.Exists(ready)) File.Delete(ready);
    }
}

static int CtrlCTerminalSupervisor(string[] args)
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.WriteLine("BLOCKED: interactive console is not attached.");
        return 3;
    }
    string resultPath = Path.GetFullPath(args[1]);
    int signals = 0;
    ConsoleCancelEventHandler keepSupervisorAlive = (_, e) =>
    {
        e.Cancel = true;
        Interlocked.Increment(ref signals);
    };
    Console.CancelKeyPress += keepSupervisorAlive;
    try
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("ctrlc-terminal-harness");
        start.ArgumentList.Add(resultPath);
        if (args.Length > 2) start.ArgumentList.Add(args[2]);
        using var harness = Process.Start(start) ?? throw new IOException("Terminal harness did not start.");
        string ready = resultPath + ".ready";
        Check(SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(10)),
            "Terminal harness did not reach its child-ready sync point.");
        Console.WriteLine("SUPERVISOR_READY_FOR_EXTERNAL_CTRL_C");
        Check(harness.WaitForExit(30000), "Terminal harness did not exit.");
        int harnessExit = harness.ExitCode;
        bool testPassed = File.Exists(resultPath) &&
            File.ReadAllText(resultPath).Contains("TEST_EXECUTOR_EXIT=0") &&
            harnessExit == 0 && signals >= 1;
        string line = $"HARNESS_PROCESS_EXIT={harnessExit} SUPERVISOR_RESULT={(testPassed ? "pass" : "fail")} " +
            $"SUPERVISOR_SIGNALS={signals}";
        Console.WriteLine(line);
        File.AppendAllText(resultPath, line + "\n");
        return testPassed ? 0 : 1;
    }
    finally { Console.CancelKeyPress -= keepSupervisorAlive; }
}

static int Probe()
{
    using var fixture = SyntheticFixture.Create();
    string parent = Path.Combine(fixture.Root, "parent");
    Directory.CreateDirectory(parent);
    string source = Path.Combine(parent, "a.txt");
    string target = Path.Combine(parent, "b.txt");
    File.WriteAllText(source, "before");

    bool movedWithoutDeleteAccess;
    using (var readOnlyDirectory = WindowsFiles.OpenDirectoryReadAttributesOnlyForTest(parent))
    {
        Directory.Move(parent, parent + "-moved");
        movedWithoutDeleteAccess = Directory.Exists(parent + "-moved");
        Directory.Move(parent + "-moved", parent);
    }
    Check(movedWithoutDeleteAccess, "The read-attribute-only directory handle did not show the expected race.");

    using (var guarded = WindowsFiles.OpenDirectory(parent))
    {
        bool moveBlocked = false;
        try { Directory.Move(parent, parent + "-moved"); }
        catch (IOException) { moveBlocked = true; }
        Check(moveBlocked, "Directory rename was not blocked by the DELETE-access guard.");
        using (var file = WindowsFiles.OpenFile(source, write: false, delete: true))
            WindowsFiles.RenameByHandle(file, target);
        Check(!File.Exists(source) && File.ReadAllText(target) == "before", "Handle rename failed.");
        using (var file = WindowsFiles.OpenFile(target, write: false, delete: true))
            WindowsFiles.DeleteByHandle(file);
        Check(!File.Exists(target), "Handle disposition did not delete the file.");
    }
    Console.WriteLine("PROBE PASS: read-only parent handle allowed rename; DELETE-access guard blocked it; handle rename/delete passed on local NTFS.");
    return 0;
}

static int All(string? filter = null)
{
    var tests = new (string Name, Action Run)[]
    {
        ("ntfs primitives", () => Probe()),
        ("modified file full chain and idempotence", Modified),
        ("modified file conflict", ModifiedConflict),
        ("equal content with changed identity conflicts", SameBytesNewIdentity),
        ("new file removal and safety copy", Added),
        ("zero-byte new file is present", EmptyAdded),
        ("new file conflict", AddedConflict),
        ("deleted file recovery", Deleted),
        ("deleted path occupied", DeletedConflict),
        ("simple rename recovery", Renamed),
        ("rename source occupied", RenamedConflict),
        ("missing baseline object", MissingObject),
        ("corrupt baseline object", CorruptObject),
        ("corrupt safety object blocks mutation", CorruptSafetyObject),
        ("name exchange is unsupported", NameExchange),
        ("missing parent is unknown", MissingParent),
        ("fake token never enters persistent plaintext", TokenPrivacy),
        ("new token safety copy stays encrypted", NewTokenPrivacy),
        ("move into excluded path stays outside coverage", MoveToExcluded),
        ("Git index and prior dirt are preserved", GitCoexistence),
        ("partial failure is in doubt", PartialFailure),
        ("cross-process state lock and termination", ProcessLock),
        ("two processes apply same plan", ConcurrentApply),
        ("crash after intent is audited", CrashAfterIntent)
        ,("crash after safety copy is audited", CrashAfterSafetyCopy)
        ,("crash after target mutation is audited", CrashAfterTargetMutation)
        ,("executor retains parent guard through verification", ExecutorGuardThroughVerification)
        ,("audited intent blocks a new plan", AuditedIntentBlocksNewPlan)
        ,("required read failure blocks child launch", RequiredReadFailureBlocksLaunch)
        ,("unsupported required file blocks child launch", UnsupportedRequiredBlocksLaunch)
        ,("final read failure is unknown", FinalReadFailureIsUnknown)
        ,("explicit external file and directory restore", ExternalScopes)
        ,("external file deletion restore", ExternalDeletion)
        ,("scope IDs separate identical relative paths", ScopeIdentity)
        ,("state inside protected scope is rejected", StateScopeRejected)
        ,("associated group requires complete selection", AssociatedGroupSelection)
        ,("whole group preflight conflict executes zero", WholeGroupPreflightConflict)
        ,("failure after first verified operation reports partial", VerifiedPartialFailure)
        ,("crash after reused safety object before reference is audited", CrashAfterSafetyObjectReuse)
        ,("R6 new encrypted object before publish crash", R6NewObjectBeforePublish)
        ,("R6 new encrypted object after publish crash", R6NewObjectAfterPublish)
        ,("R6 new encrypted temporary file creation crash", R6NewObjectTempCreated)
        ,("R6 new encrypted object verification crash", R6NewObjectAfterVerified)
        ,("crash after plan commit keeps fixed plan", CrashAfterPlanCommit)
        ,("crash after verification is audited", CrashAfterVerification)
        ,("binary file restore", BinaryRestore)
        ,("exe wrapper preserves arguments stdin stdout and nonzero exit", ExeWrapper)
        ,("cmd wrapper preserves space arguments and exit", CmdWrapper)
        ,("cmd quote argument is rejected before launch", CmdQuoteRejected)
        ,("abnormal child exit remains reportable", AbnormalChildExit)
        ,("missing safety after restart blocks new apply", MissingSafetyAfterRestart)
        ,("parent move at durable intent blocks mutation", ParentMoveAtIntent)
        ,("target replacement during verification is blocked", TargetRaceAtVerification)
        ,("corrupt baseline before ready blocks child", CorruptBaselineBeforeReady)
        ,("overwrite rename is not split into independent changes", OverwriteRenameRejected)
        ,("coverage persists four separate buckets and rule version", CoverageBuckets)
        ,("ordinary CLI run remains closed", OrdinaryCliClosed)
        ,("R1 hard link after intent rejects without alias write", R1HardLinkAtIntent)
        ,("R2 changed target object invalidates confirmed plan", R2ChangedTargetObject)
        ,("R2 changed scope invalidates confirmed plan", R2ChangedScope)
        ,("R3 baseline ADS blocks child", R3BaselineAds)
        ,("R3 ADS added before apply blocks mutation", R3AdsBeforeApply)
        ,("R4 recreated root rejects deleted file restore", R4RecreatedRoot)
        ,("R4 recreated nested parent rejects restore", R4RecreatedParent)
        ,("R5 same-content replacement cannot be verified", R5SameContentReplacement)
        ,("R1 final attribute change rejects modification", R1AttributesAtIntent)
        ,("R1 hard link blocks added-file removal", R1AddedHardLink)
        ,("R1 hard link blocks rename restoration", R1RenamedHardLink)
        ,("R1 capture rejects leaf symlink swap", R1CaptureSymlinkSwap)
        ,("R1 capture rejects hard-link leaf swap", R1CaptureHardLinkSwap)
        ,("R2 changed operation kind invalidates plan", R2ChangedOperationKind)
        ,("R2 interrupted audit uses confirmed path", R2InterruptedAuditConfirmedPath)
        ,("R3 ADS on added file blocks removal", R3AddedAds)
        ,("R3 stream query failure is unknown", R3StreamQueryFailure)
        ,("R4 recreated external directory is incomplete", R4ExternalDirectory)
        ,("R4 empty directory history is preserved", R4EmptyDirectory)
        ,("R4 newly added directory is unsupported", R4NewDirectory)
        ,("R4 new directory does not receive invented absent baseline", R4NewDirectoryFile)
        ,("R4 root replacement before apply conflicts", R4RootChangedBeforeApply)
        ,("R4 root reparent at durable intent rejects mutation", R4RootReparentAtIntent)
        ,("R4 intermediate reparent at durable intent rejects mutation", R4IntermediateReparentAtIntent)
        ,("R4 ordinary nested file restores", R4OrdinaryNestedRestore)
        ,("R4 root reparent blocks added removal", () => R4RootReparentOtherKind(ChangeKind.Added))
        ,("R4 root reparent blocks deleted recreation", () => R4RootReparentOtherKind(ChangeKind.Deleted))
        ,("R4 root reparent blocks simple rename", () => R4RootReparentOtherKind(ChangeKind.Renamed))
        ,("three-name cycle is unsupported", ThreeNameCycleRejected)
        ,("cross-parent rename is unsupported", CrossParentRenameRejected)
        ,("cmd metacharacters reject before launch", CmdMetacharactersRejected)
        ,("cmd raw arguments reject before launch", CmdRawArgumentsRejected)
        ,("cancel during baseline prevents child launch", CancelDuringBaseline)
        ,("cancel during final scan remains incomplete", CancelDuringFinalScan)
        ,("cancel after child exit with finalization failure preserves code", CancelThenFinalizationFailure)
        ,("unresponsive child cancellation is bounded", UnresponsiveChildCancellation)
        ,("terminated wrapper leaves interrupted session", TerminatedWrapperAudit)
        ,("failed final commit cannot report complete", FailedFinalCommit)
        ,("persistent final save failure audits interrupted", PersistentFinalSaveFailure)
        ,("Blast failure process exit overrides known child code", BlastFailureProcessExit)
        ,("unconfirmed direct child blocks later run and apply", UnconfirmedChildBlocksMutations)
        ,("terminated wrapper child blocks later run and apply", TerminatedWrapperBlocksMutations)
        ,("running save failure retains uncertain child barrier", RunningSaveFailureBlocksMutations)
        ,("prelaunch durable failure prevents child start", PrelaunchDurableFailurePreventsStart)
        ,("baseline cancellation permits later run", BaselineCancellationPermitsLaterRun)
        ,("confirmed completion permits later run", ConfirmedCompletionPermitsLaterRun)
        ,("real SQLite double failure returns wrapper error", RealSqliteDoubleFailure)
        ,("missing object key preserves existing store", MissingObjectKey)
        ,("report separates supported kind from object integrity and apply state", ReportEligibility)
    };
    int failures = 0, passed = 0, skipped = 0, blocked = 0;
    var selected = tests.Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (selected.Length == 0)
    {
        Console.Error.WriteLine("FAIL no test matches filter: " + filter);
        Console.WriteLine("RESULT: Passed=0 Failed=1 Skipped=0 Blocked=0");
        return 1;
    }
    foreach (var (name, run) in selected)
    {
        try { run(); passed++; Console.WriteLine("PASS " + name); }
        catch (TestBlockedException ex) { blocked++; Console.WriteLine("BLOCKED " + name + ": " + ex.Message); }
        catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
    }
    Console.WriteLine($"RESULT: Passed={passed} Failed={failures} Skipped={skipped} Blocked={blocked}");
    return failures == 0 && blocked == 0 ? 0 : 1;
}

static int Demo()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "modify"));
    var report = engine.Report(run.SessionId);
    var change = OnlyChange(report, ChangeKind.Modified);
    var plan = engine.Preview(run.SessionId, [change.Id]);
    var applied = engine.Apply(plan.Id, plan.Hash);
    Console.WriteLine($"session={run.SessionId} child_exit_code={run.ChildExitCode} blast_status={run.BlastStatus}");
    Console.WriteLine($"change_id={change.Id} attribution={change.Attribution} plan_id={plan.Id}");
    Console.WriteLine($"apply={applied.Status} operations_applied={applied.OperationsApplied} restored={File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt"))}");
    Check(applied.OperationsApplied == 1, "Demo did not execute one recovery operation.");
    return 0;
}

static ProcessStartInfo Child(SyntheticFixture fixture, string mutation, string? payload = null)
{
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("mutate");
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    command.ArgumentList.Add(mutation);
    if (payload is not null) command.ArgumentList.Add(payload);
    return command;
}

static Process Worker(string kind, SyntheticFixture fixture, params string[] extra)
{
    var command = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardError = true
    };
    command.ArgumentList.Add(kind);
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    foreach (string item in extra) command.ArgumentList.Add(item);
    return Process.Start(command) ?? throw new IOException("Fixture worker did not start.");
}

static (SyntheticFixture Fixture, SessionEngine Engine, SessionRecord Report) Setup(string initial, string mutation)
{
    var fixture = SyntheticFixture.Create();
    try
    {
        if (initial != "<absent>") File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), initial);
        var engine = new SessionEngine(fixture);
        var run = engine.Run(Child(fixture, mutation));
        Check(run.BlastStatus == "ok" && run.ChildExitCode == 0, "Synthetic run failed: " + run.BlastStatus);
        return (fixture, engine, engine.Report(run.SessionId));
    }
    catch { fixture.Dispose(); throw; }
}

static ChangeRecord OnlyChange(SessionRecord report, ChangeKind kind)
{
    Check(report.Changes?.Count == 1, "Expected exactly one change.");
    Check(report.Changes![0].Kind == kind, "Unexpected change kind: " + report.Changes[0].Kind);
    return report.Changes[0];
}

static void Modified()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var change = OnlyChange(report, ChangeKind.Modified);
        Check(engine.ReportJson(report.Id).Contains(change.Id) &&
              engine.ReportText(report.Id).Contains("final-state differences only"),
              "Reports did not expose change ID and observation limit.");
        Check(change.Attribution == "temporally_correlated", "Attribution was overstated.");
        var empty = engine.Preview(report.Id, []);
        Check(engine.Apply(empty.Id, empty.Hash).OperationsApplied == 0, "Empty selection executed.");
        var plan = engine.Preview(report.Id, [change.Id]);
        Throws<InvalidOperationException>(() => engine.Apply(plan.Id, new string('0', 64)));
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.Status == "verified" && result.OperationsApplied == 1, "Modification was not verified.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "before", "Baseline content was not restored.");
        var saved = result.Operations.Single().SafetyObjectId;
        Check(saved is not null && Encoding.UTF8.GetString(engine.Objects.Read(saved)) == "after", "Safety copy is wrong.");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(engine.Objects.PathForTest(saved!))).Contains("after"),
            "Safety object contains plaintext.");
        var repeat = engine.Apply(plan.Id, plan.Hash);
        Check(repeat.OperationsApplied == 0 && repeat.Status == "no operations applied", "Repeat was not idempotent.");
    }
}

static void ModifiedConflict()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "user change");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && result.Status == "no operations applied", "Conflict was overwritten.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "user change", "User content changed.");
    }
}

static void SameBytesNewIdentity()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string path = Path.Combine(fixture.Root, "alpha.txt");
        File.Delete(path);
        File.WriteAllText(path, "after");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && File.ReadAllText(path) == "after", "Changed file identity was ignored.");
    }
}

static void Added() => AddedCore("add", 11);
static void EmptyAdded() => AddedCore("add-empty", 0);
static void AddedCore(string mutation, long expectedLength)
{
    var (fixture, engine, report) = Setup("<absent>", mutation);
    using (fixture)
    {
        var change = OnlyChange(report, ChangeKind.Added);
        Check(change.Baseline.Presence == Presence.Absent && change.Baseline.ObjectId is null,
            "Added file baseline must be Absent without an object.");
        Check(change.Final.Presence == Presence.Present && change.Final.Length == expectedLength &&
              change.Final.ObjectId is not null, "New file present state is wrong.");
        var plan = engine.Preview(report.Id, [change.Id]);
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.Status == "verified" && result.OperationsApplied == 1, "New file removal did not verify.");
        Check(!File.Exists(Path.Combine(fixture.Root, "new.txt")), "New file still exists.");
        Check(result.Operations.Single().SafetyObjectId is not null, "Present C was not saved.");
    }
}

static void AddedConflict()
{
    var (fixture, engine, report) = Setup("<absent>", "add");
    using (fixture)
    {
        File.WriteAllText(Path.Combine(fixture.Root, "new.txt"), "later user edit");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Added).Id]);
        Check(engine.Apply(plan.Id, plan.Hash).OperationsApplied == 0, "New file conflict executed.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "new.txt")) == "later user edit", "Later edit lost.");
    }
}

static void Deleted()
{
    var (fixture, engine, report) = Setup("before", "delete");
    using (fixture)
    {
        var change = OnlyChange(report, ChangeKind.Deleted);
        Check(change.Final.Presence == Presence.Absent && change.Final.ObjectId is null, "Deleted A is not Absent.");
        var plan = engine.Preview(report.Id, [change.Id]);
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.Status == "verified" && result.OperationsApplied == 1, "Deleted file recovery did not verify.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "before", "Deleted content not restored.");
        Check(result.Operations.Single().SafetyObjectId is null, "An absent C got a fake safety object.");
        Check(result.Operations.Single().ExpectedPrePresence == Presence.Absent &&
              result.Operations.Single().ExpectedPreParentId != 0,
            "Absent precondition was not persisted.");
    }
}

static void DeletedConflict()
{
    var (fixture, engine, report) = Setup("before", "delete");
    using (fixture)
    {
        File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "occupied");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Deleted).Id]);
        Check(engine.Apply(plan.Id, plan.Hash).OperationsApplied == 0, "Occupied path was overwritten.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "occupied", "Occupied content changed.");
    }
}

static void Renamed()
{
    var (fixture, engine, report) = Setup("before", "rename");
    using (fixture)
    {
        var change = OnlyChange(report, ChangeKind.Renamed);
        var plan = engine.Preview(report.Id, [change.Id]);
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.Status == "verified" && result.OperationsApplied == 1, "Rename recovery did not verify.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "before" &&
              !File.Exists(Path.Combine(fixture.Root, "beta.txt")), "Rename paths are wrong.");
    }
}

static void RenamedConflict()
{
    var (fixture, engine, report) = Setup("before", "rename");
    using (fixture)
    {
        File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "occupied");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Renamed).Id]);
        Check(engine.Apply(plan.Id, plan.Hash).OperationsApplied == 0, "Rename conflict executed.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "occupied" &&
              File.ReadAllText(Path.Combine(fixture.Root, "beta.txt")) == "before", "Rename conflict damaged paths.");
    }
}

static void MissingObject()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var change = OnlyChange(report, ChangeKind.Modified);
        var plan = engine.Preview(report.Id, [change.Id]);
        File.Delete(engine.Objects.PathForTest(change.Baseline.ObjectId!));
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && result.Status == "no operations applied", "Missing object did not block apply.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after", "Target changed with missing object.");
    }
}

static void CorruptObject()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var change = OnlyChange(report, ChangeKind.Modified);
        var plan = engine.Preview(report.Id, [change.Id]);
        File.WriteAllBytes(engine.Objects.PathForTest(change.Baseline.ObjectId!), [1, 2, 3]);
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "Corrupt object did not block apply.");
    }
}

static void CorruptSafetyObject()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        engine.BoundaryForTest = boundary =>
        {
            if (boundary != "safety_copied") return;
            var recorded = engine.State.LoadPlan(plan.Id).Operations.Single();
            File.WriteAllBytes(engine.Objects.PathForTest(recorded.SafetyObjectId!), [1, 2, 3]);
        };
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "A corrupt safety copy did not block mutation.");
        Check(engine.State.LoadPlan(plan.Id).Status == "in_doubt", "Safety-copy failure was not journaled.");
    }
}

static void NameExchange()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "alpha");
    File.WriteAllText(Path.Combine(fixture.Root, "bravo.txt"), "bravo");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "swap"));
    var report = engine.Report(run.SessionId);
    Check(report.Changes?.Count == 2 && report.Changes.All(c => c.Kind == ChangeKind.Unsupported),
        "Name exchange was split into independent restorable modifications.");
    Throws<NotSupportedException>(() => engine.Preview(run.SessionId, [report.Changes![0].Id]));
    Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "bravo" &&
          File.ReadAllText(Path.Combine(fixture.Root, "bravo.txt")) == "alpha", "Rejected swap changed content.");
}

static void MissingParent()
{
    using var fixture = SyntheticFixture.Create();
    string nested = Path.Combine(fixture.Root, "nested");
    Directory.CreateDirectory(nested);
    File.WriteAllText(Path.Combine(nested, "alpha.txt"), "before");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "delete-parent"));
    var report = engine.Report(run.SessionId);
    Check(report.Status == "incomplete" && report.Final!.Values.Any(f => f.Presence == Presence.Unknown),
        "Missing parent was misclassified as Absent.");
    Throws<InvalidOperationException>(() => engine.Preview(run.SessionId, []));
}

static void TokenPrivacy()
{
    using var fixture = SyntheticFixture.Create();
    string token = "FAKE_TOKEN_" + Guid.NewGuid().ToString("N");
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), token);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "modify"));
    var report = engine.Report(run.SessionId);
    var plan = engine.Preview(run.SessionId, [OnlyChange(report, ChangeKind.Modified).Id]);
    Check(engine.Apply(plan.Id, plan.Hash).OperationsApplied == 1, "Token fixture did not restore.");
    foreach (string file in Directory.GetFiles(fixture.StateDirectory, "*", SearchOption.AllDirectories))
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(token),
            "Persistent plaintext token found in " + Path.GetFileName(file));
}

static void NewTokenPrivacy()
{
    using var fixture = SyntheticFixture.Create();
    string token = "FAKE_TOKEN_" + Guid.NewGuid().ToString("N");
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "token-after", token));
    var report = engine.Report(run.SessionId);
    var plan = engine.Preview(run.SessionId, [OnlyChange(report, ChangeKind.Modified).Id]);
    var result = engine.Apply(plan.Id, plan.Hash);
    Check(result.OperationsApplied == 1 && File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "before",
        "New token fixture did not restore.");
    Check(Encoding.UTF8.GetString(engine.Objects.Read(result.Operations.Single().SafetyObjectId!)) == token,
        "Safety object did not preserve the new content.");
    foreach (string file in Directory.GetFiles(fixture.StateDirectory, "*", SearchOption.AllDirectories))
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(token),
            "Session token leaked to persistent plaintext.");
}

static void GitCoexistence()
{
    using var fixture = SyntheticFixture.Create();
    Git(fixture.Root, "init");
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "committed");
    Git(fixture.Root, "add", "alpha.txt");
    Git(fixture.Root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "prior dirty");
    File.WriteAllText(Path.Combine(fixture.Root, "untracked.txt"), "prior untracked");
    File.WriteAllText(Path.Combine(fixture.Root, ".gitignore"), "untracked.txt\n");
    string headBefore = Git(fixture.Root, "rev-parse", "HEAD");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "git-mutate"));
    var report = engine.Report(run.SessionId);
    Check(report.Changes?.Count == 2 && report.Baseline.ContainsKey("untracked.txt"),
        "Prior untracked or ignored file was not in the baseline.");
    string indexAfterCommand = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(Path.Combine(fixture.Root, ".git", "index"))));
    var plan = engine.Preview(run.SessionId, report.Changes!.Select(c => c.Id));
    var result = engine.Apply(plan.Id, plan.Hash);
    Check(result.Status == "verified" && result.OperationsApplied == 2, "Git fixture selected changes did not restore.");
    Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "prior dirty" &&
          File.ReadAllText(Path.Combine(fixture.Root, "untracked.txt")) == "prior untracked",
        "Prior working tree contents were lost.");
    Check(Git(fixture.Root, "rev-parse", "HEAD") == headBefore &&
          Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
              File.ReadAllBytes(Path.Combine(fixture.Root, ".git", "index")))) == indexAfterCommand,
        "Blast altered Git HEAD or index.");
}

static string Git(string directory, params string[] arguments)
{
    var info = new ProcessStartInfo("git")
    {
        WorkingDirectory = directory, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new IOException("Git fixture process did not start.");
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new IOException("Git fixture command failed: " + error);
    return output.Trim();
}

static void MoveToExcluded()
{
    using var fixture = SyntheticFixture.Create();
    string token = "FAKE_TOKEN_" + Guid.NewGuid().ToString("N");
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), token);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "move-to-excluded"));
    var report = engine.Report(run.SessionId);
    Check(report.Final!.Keys.All(path => !path.Equals(".env", StringComparison.OrdinalIgnoreCase)),
        "Excluded destination was silently included.");
    Check(File.ReadAllText(Path.Combine(fixture.Root, ".env")) == token, "Fixture source did not move.");
    foreach (string file in Directory.GetFiles(fixture.StateDirectory, "*", SearchOption.AllDirectories))
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(token),
            "Excluded-path move leaked plaintext into state store.");
}

static void PartialFailure()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    File.WriteAllText(Path.Combine(fixture.Root, "bravo.txt"), "before bravo");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "double-modify"));
    var report = engine.Report(run.SessionId);
    Check(report.Changes?.Count == 2, "Expected two modified files.");
    var plan = engine.Preview(run.SessionId, report.Changes!.Select(c => c.Id));
    engine.BoundaryForTest = stage => { if (stage == "target_modified") throw new IOException("Injected post-write failure."); };
    var result = engine.Apply(plan.Id, plan.Hash);
    Check(result.Status.Contains("in doubt") && result.OperationsApplied == 0, "Partial failure was hidden.");
    Check(engine.State.LoadPlan(plan.Id).Operations.Any(o => o.Status == "in_doubt"), "Journal lost uncertain operation.");
    Check(result.Operations.Count(o => o.Status == "verified") == 0, "Unverified work marked verified.");
    string alpha = File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt"));
    string bravo = File.ReadAllText(Path.Combine(fixture.Root, "bravo.txt"));
    Check((alpha == "before" && bravo == "after bravo") ||
          (alpha == "after" && bravo == "before bravo"),
        "Injected failure did not preserve exactly one restored and one unexecuted file.");
}

static void ProcessLock()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    using var worker = Worker("hold-lock", fixture);
    WaitFor(Path.Combine(fixture.StateDirectory, "held.signal"));
    Throws<StoreBusyException>(() => engine.State.AcquireLock().Dispose());
    worker.Kill();
    worker.WaitForExit();
    using var reacquired = engine.State.AcquireLock();
    engine.State.AuditUnresolved();
}

static void ConcurrentApply()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        using var worker = Worker("apply-worker", fixture, plan.Id, plan.Hash, "wait");
        WaitFor(Path.Combine(fixture.StateDirectory, "held.signal"));
        Throws<StoreBusyException>(() => engine.Apply(plan.Id, plan.Hash));
        File.WriteAllText(Path.Combine(fixture.StateDirectory, "release.signal"), "release");
        Check(worker.WaitForExit(10000) && worker.ExitCode == 0, "First apply worker did not finish.");
        var repeat = engine.Apply(plan.Id, plan.Hash);
        Check(repeat.OperationsApplied == 0 && File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "before",
            "Second apply was not an idempotent no-op.");
    }
}

static void CrashAfterIntent()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        using var worker = Worker("apply-worker", fixture, plan.Id, plan.Hash, "crash-intent_durable");
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Crash worker did not terminate at intent.");
        using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
        var audited = engine.State.LoadPlan(plan.Id);
        Check(audited.Status == "in_doubt" && audited.Operations.Single().SafetyObjectId is not null,
            "Interrupted intent or safety copy was not preserved.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after", "Target changed before crash point.");
        Throws<InvalidOperationException>(() => engine.Apply(plan.Id, plan.Hash));
    }
}

static void CrashAfterSafetyCopy()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        using var worker = Worker("apply-worker", fixture, plan.Id, plan.Hash, "crash-safety_copied");
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Worker did not terminate after safety copy.");
        using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
        var operation = engine.State.LoadPlan(plan.Id).Operations.Single();
        Check(operation.Status == "interrupted_before_intent" && operation.SafetyObjectId is not null,
            "Pre-intent interruption was not classified.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after", "Target changed before intent.");
    }
}

static void CrashAfterTargetMutation()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        using var worker = Worker("apply-worker", fixture, plan.Id, plan.Hash, "crash-target_modified");
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Worker did not terminate after mutation.");
        using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
        var audited = engine.State.LoadPlan(plan.Id);
        Check(audited.Status == "in_doubt" && audited.Operations.Single().Status == "in_doubt",
            "Post-mutation interruption was marked complete.");
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "before", "Mutation point did not execute.");
        Throws<InvalidOperationException>(() => engine.Apply(plan.Id, plan.Hash));
    }
}

static void WaitFor(string path)
{
    var timer = Stopwatch.StartNew();
    while (!File.Exists(path))
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Worker signal did not arrive.");
        Thread.Sleep(20);
    }
}

static void ExecutorGuardThroughVerification()
{
    using var fixture = SyntheticFixture.Create();
    string parent = Path.Combine(fixture.Root, "nested");
    Directory.CreateDirectory(parent);
    File.WriteAllText(Path.Combine(parent, "item.bin"), "before");
    var engine = new SessionEngine(fixture);
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("mutate-nested");
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    var run = engine.Run(command);
    Check(run.BlastStatus == "ok", "Nested run failed.");
    var change = OnlyChange(engine.Report(run.SessionId), ChangeKind.Modified);
    var plan = engine.Preview(run.SessionId, [change.Id]);
    bool moved = false;
    engine.BoundaryForTest = stage =>
    {
        if (stage != "target_modified") return;
        try { Directory.Move(parent, parent + "-moved"); moved = true; }
        catch (IOException) { }
        finally { if (moved) Directory.Move(parent + "-moved", parent); }
    };
    var result = engine.Apply(plan.Id, plan.Hash);
    Check(!moved && result.OperationsApplied == 1 &&
          File.ReadAllText(Path.Combine(parent, "item.bin")) == "before",
        "Executor released parent protection before post-operation verification.");
}

static void AuditedIntentBlocksNewPlan()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string id = OnlyChange(report, ChangeKind.Modified).Id;
        var first = engine.Preview(report.Id, [id]);
        using var worker = Worker("apply-worker", fixture, first.Id, first.Hash, "crash-intent_durable");
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Intent crash did not occur.");
        var second = engine.Preview(report.Id, [id]);
        Throws<InvalidOperationException>(() => engine.Apply(second.Id, second.Hash));
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "A new plan modified an unresolved target.");
    }
}

static void RequiredReadFailureBlocksLaunch()
{
    using var fixture = SyntheticFixture.Create();
    string file = Path.Combine(fixture.Root, "alpha.txt");
    File.WriteAllText(file, "required");
    using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "baseline_failed" && run.ChildExitCode is null &&
          report.Status == "failed" && report.CoverageDetail.ReadFailures.Contains("alpha.txt") &&
          !File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
        "A required read failure entered ready or launched the child.");
}

static void UnsupportedRequiredBlocksLaunch()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllBytes(Path.Combine(fixture.Root, "large.bin"), new byte[FileSystemScope.MaxFileBytes + 1]);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "baseline_failed" &&
          report.CoverageDetail.UnsupportedTypes.Contains("large.bin") &&
          !report.CoverageDetail.ConfirmedExclusions.Contains("large.bin") &&
          !File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
        "Unsupported required file became an exclusion or launched the child.");
}

static void FinalReadFailureIsUnknown()
{
    using var fixture = SyntheticFixture.Create();
    string file = Path.Combine(fixture.Root, "alpha.txt");
    File.WriteAllText(file, "before");
    var engine = new SessionEngine(fixture);
    FileStream? locked = null;
    engine.BoundaryForTest = stage =>
    {
        if (stage == "before_final_scan")
            locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    };
    try
    {
        var run = engine.Run(Child(fixture, "modify"));
        var report = engine.Report(run.SessionId);
        Check(run.BlastStatus == "final_scan_incomplete" && report.Coverage == "incomplete" &&
              report.Final!["alpha.txt"].Presence == Presence.Unknown &&
              report.Final["alpha.txt"].Presence != Presence.Absent,
            "Final read failure was treated as deletion or complete scan.");
        Throws<InvalidOperationException>(() => engine.Preview(report.Id, []));
    }
    finally { locked?.Dispose(); }
}

static void ExternalScopes()
{
    using var fixture = SyntheticFixture.Create();
    string fileParent = Path.Combine(fixture.DirectoryPath, "outside-file");
    string directory = Path.Combine(fixture.DirectoryPath, "outside-dir");
    Directory.CreateDirectory(fileParent);
    Directory.CreateDirectory(directory);
    string selected = Path.Combine(fileParent, "chosen.txt");
    string neighbor = Path.Combine(fileParent, "neighbor.txt");
    File.WriteAllText(selected, "before external");
    File.WriteAllText(neighbor, "neighbor before");
    fixture.AddExternalFile(selected);
    fixture.AddExternalDirectory(directory);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "external-modify-add"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "ok" && report.Changes?.Count == 2 &&
          report.Baseline.ContainsKey("external-file|chosen.txt") &&
          !report.Baseline.ContainsKey("external-file|neighbor.txt") &&
          report.Changes.Any(c => c.Path == "external-dir|new.txt" && c.Kind == ChangeKind.Added) &&
          engine.ReportJson(report.Id).Contains("external_directory"),
        "External scope coverage or report was incorrect.");
    var plan = engine.Preview(report.Id, report.Changes!.Select(c => c.Id));
    var applied = engine.Apply(plan.Id, plan.Hash);
    Check(applied.Status == "verified" && applied.OperationsApplied == 2 &&
          File.ReadAllText(selected) == "before external" && !File.Exists(Path.Combine(directory, "new.txt")) &&
          File.ReadAllText(neighbor) == "neighbor after" &&
          !report.Final!.ContainsKey("external-file|neighbor.txt"),
        "External restore expanded to an unselected neighbor or failed to restore selected files.");
}

static void ExternalDeletion()
{
    using var fixture = SyntheticFixture.Create();
    string parent = Path.Combine(fixture.DirectoryPath, "outside-file");
    Directory.CreateDirectory(parent);
    string selected = Path.Combine(parent, "chosen.txt");
    File.WriteAllText(selected, "before external");
    fixture.AddExternalFile(selected);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "external-delete"));
    var report = engine.Report(run.SessionId);
    var change = OnlyChange(report, ChangeKind.Deleted);
    Check(change.Path == "external-file|chosen.txt", "Deleted external scope path lost its ID.");
    var plan = engine.Preview(report.Id, [change.Id]);
    var applied = engine.Apply(plan.Id, plan.Hash);
    Check(applied.Status == "verified" && applied.OperationsApplied == 1 &&
          File.ReadAllText(selected) == "before external" &&
          applied.Operations.Single().SafetyObjectId is null,
        "External deletion restore or absent-C safety behavior failed.");
}

static void ScopeIdentity()
{
    using var fixture = SyntheticFixture.Create();
    string external = Path.Combine(fixture.DirectoryPath, "outside-dir");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before workspace");
    File.WriteAllText(Path.Combine(external, "alpha.txt"), "before external");
    fixture.AddExternalDirectory(external);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "external-same-name"));
    var report = engine.Report(run.SessionId);
    Check(report.Changes?.Count == 2 && report.Changes.Select(c => c.Id).Distinct().Count() == 2,
        "Same relative path merged across scopes.");
    var externalChange = report.Changes!.Single(c => c.Path == "external-dir|alpha.txt");
    var plan = engine.Preview(report.Id, [externalChange.Id]);
    var applied = engine.Apply(plan.Id, plan.Hash);
    Check(applied.OperationsApplied == 1 && File.ReadAllText(Path.Combine(external, "alpha.txt")) == "before external" &&
          File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after workspace",
        "Selected external path modified the workspace sibling.");
}

static void StateScopeRejected()
{
    using var fixture = SyntheticFixture.Create();
    fixture.AddExternalDirectory(fixture.StateDirectory);
    var engine = new SessionEngine(fixture);
    Throws<InvalidOperationException>(() => engine.Run(Child(fixture, "mark")));
    Check(!File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
        "Child launched with state store in protection scope.");
}

static (SyntheticFixture Fixture, SessionEngine Engine, SessionRecord Report) TwoModified()
{
    var fixture = SyntheticFixture.Create();
    try
    {
        File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
        File.WriteAllText(Path.Combine(fixture.Root, "bravo.txt"), "before bravo");
        var engine = new SessionEngine(fixture);
        var run = engine.Run(Child(fixture, "double-modify"));
        Check(run.BlastStatus == "ok", "Two-file run failed.");
        return (fixture, engine, engine.Report(run.SessionId));
    }
    catch { fixture.Dispose(); throw; }
}

static void AssociatedGroupSelection()
{
    var (fixture, engine, report) = TwoModified();
    using (fixture)
    {
        Check(report.Changes?.Count == 2, "Expected two changes.");
        report.Changes = report.Changes!.Select(c => c with { GroupId = "synthetic-associated-group" }).ToList();
        engine.State.SaveSession(report);
        Throws<InvalidOperationException>(() => engine.Preview(report.Id, [report.Changes[0].Id]));
        var plan = engine.Preview(report.Id, report.Changes.Select(c => c.Id));
        var applied = engine.Apply(plan.Id, plan.Hash);
        Check(applied.OperationsApplied == 2 && applied.Status == "verified", "Complete group did not restore.");
    }
}

static void WholeGroupPreflightConflict()
{
    var (fixture, engine, report) = TwoModified();
    using (fixture)
    {
        string alpha = Path.Combine(fixture.Root, "alpha.txt");
        File.WriteAllText(alpha, "third-party change");
        var plan = engine.Preview(report.Id, report.Changes!.Select(c => c.Id));
        var applied = engine.Apply(plan.Id, plan.Hash);
        Check(applied.OperationsApplied == 0 && applied.Status == "no operations applied" &&
              applied.Operations.All(o => o.Status == "conflict") &&
              File.ReadAllText(alpha) == "third-party change" &&
              File.ReadAllText(Path.Combine(fixture.Root, "bravo.txt")) == "after bravo",
            "Preflight conflict allowed part of the group to execute.");
    }
}

static void VerifiedPartialFailure()
{
    var (fixture, engine, report) = TwoModified();
    using (fixture)
    {
        var plan = engine.Preview(report.Id, report.Changes!.Select(c => c.Id));
        int durableIntents = 0;
        engine.BoundaryForTest = stage =>
        {
            if (stage == "intent_durable" && ++durableIntents == 2)
                throw new IOException("Injected second-operation failure.");
        };
        var applied = engine.Apply(plan.Id, plan.Hash);
        Check(applied.OperationsApplied == 1 && applied.Status == "partial; in doubt" &&
              applied.Operations.Count(o => o.Status == "verified") == 1 &&
              applied.Operations.Count(o => o.Status == "in_doubt") == 1 &&
              new[] { File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")),
                      File.ReadAllText(Path.Combine(fixture.Root, "bravo.txt")) }.Count(x => x.StartsWith("before")) == 1,
            "Execution failure lost partial count or changed the unexecuted file.");
    }
}

static void CrashAfterSafetyObjectReuse()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        using var worker = Worker("apply-worker", fixture, plan.Id, plan.Hash, "crash-safety_object_published");
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Object publication crash did not occur.");
        using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
        var audited = engine.State.LoadPlan(plan.Id);
        Check(audited.Status == "in_doubt" && audited.Operations.Single().Status == "interrupted_before_intent" &&
              audited.Operations.Single().SafetyObjectId is null &&
              File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "Published orphan object was treated as a durable intent or mutated target.");
    }
}

static void R6NewObjectBeforePublish() => R6NewObjectBoundary("encrypted_temp_flushed", published: false);

static void R6NewObjectAfterPublish() => R6NewObjectBoundary("new_object_published", published: true);

static void R6NewObjectTempCreated() => R6NewObjectBoundary("encrypted_temp_created", published: false);

static void R6NewObjectAfterVerified() => R6NewObjectBoundary("new_object_verified", published: true);

static void R6NewObjectBoundary(string boundary, bool published)
{
    using var fixture = SyntheticFixture.Create();
    string target = Path.Combine(fixture.Root, "alpha.txt");
    File.WriteAllText(target, "untouched");
    _ = new SessionEngine(fixture); // Initializes the synthetic store and key.
    string fakeToken = "FAKE_NEW_OBJECT_" + Guid.NewGuid().ToString("N");
    string objectId = ObjectStore.Hash(Encoding.UTF8.GetBytes(fakeToken));
    string objectPath = Path.Combine(fixture.StateDirectory, "objects", objectId + ".bro");
    Check(!File.Exists(objectPath), "New-object fixture unexpectedly hit deduplication.");
    using var worker = Worker("object-save-worker", fixture, boundary, fakeToken);
    Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "New-object worker did not terminate.");
    Check(File.ReadAllText(Path.Combine(fixture.StateDirectory, "object-branch.signal")) == boundary,
        "New-object publication branch was not reached.");
    string[] pending = Directory.GetFiles(Path.Combine(fixture.StateDirectory, "objects"), ".pending-*");
    Check(File.Exists(objectPath) == published && pending.Length == (published ? 0 : 1) &&
          File.ReadAllText(target) == "untouched",
        "Publication boundary changed target or left unexpected object state.");
    foreach (string file in pending.Concat(File.Exists(objectPath) ? [objectPath] : []))
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(fakeToken),
            "New-object branch wrote unexpected plaintext.");
    var reopened = new ObjectStore(fixture.StateDirectory);
    if (published) reopened.Verify(objectId, Encoding.UTF8.GetByteCount(fakeToken));
    var state = new StateStore(fixture.StateDirectory);
    using var held = state.AcquireLock();
    var audit = state.InspectObjects();
    Check(audit.PendingEncryptedFiles.Count == (published ? 0 : 1) &&
          audit.UnreferencedPublishedObjects.Count == (published ? 1 : 0) &&
          (!published || audit.UnreferencedPublishedObjects.Single() == objectId),
        "Object audit did not report the pending or unreferenced publication state.");
}

static void CrashAfterPlanCommit()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string changeId = OnlyChange(report, ChangeKind.Modified).Id;
        using var worker = Worker("preview-worker", fixture, report.Id, changeId);
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Plan commit crash did not occur.");
        var plans = engine.State.PlansForSession(report.Id);
        Check(plans.Count == 1 && plans[0].Status == "planned" && plans[0].ChangeIds.SequenceEqual([changeId]) &&
              plans[0].Hash.Length == 64 && File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "Fixed plan was not durable before crash or target was changed.");
    }
}

static void CrashAfterVerification()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        using var worker = Worker("apply-worker", fixture, plan.Id, plan.Hash, "crash-verified");
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Post-verification crash did not occur.");
        using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
        var audited = engine.State.LoadPlan(plan.Id);
        Check(audited.Status == "in_doubt" && audited.Operations.Single().Status == "verified" &&
              File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "before",
            "Verified operation or uncertain plan status was lost.");
    }
}

static void BinaryRestore()
{
    using var fixture = SyntheticFixture.Create();
    byte[] before = [0, 1, 2, 0, 255, 128, 64];
    string file = Path.Combine(fixture.Root, "alpha.txt");
    File.WriteAllBytes(file, before);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "binary-modify"));
    var change = OnlyChange(engine.Report(run.SessionId), ChangeKind.Modified);
    var plan = engine.Preview(run.SessionId, [change.Id]);
    var applied = engine.Apply(plan.Id, plan.Hash);
    Check(applied.Status == "verified" && applied.OperationsApplied == 1 &&
          File.ReadAllBytes(file).SequenceEqual(before), "Binary restoration failed.");
}

static void ExeWrapper()
{
    using var fixture = SyntheticFixture.Create();
    var info = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    info.ArgumentList.Add("stdio-wrapper");
    info.ArgumentList.Add(fixture.DirectoryPath);
    info.ArgumentList.Add(fixture.WorkerToken);
    using var process = Process.Start(info) ?? throw new IOException("Wrapper did not start.");
    process.StandardInput.WriteLine("from stdin");
    process.StandardInput.Close();
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    Check(process.WaitForExit(10000), "Wrapper hung on inherited stdin.");
    Check(process.ExitCode == 17 && output.Contains("CHILD_OUTPUT:space value|quoted \"value\"|from stdin") &&
          output.Contains("WRAPPER_STATUS:ok:17:redirected_input_no_keyboard_control") &&
          File.ReadAllText(Path.Combine(fixture.Root, "stdio-result.txt")) ==
            "space value|quoted \"value\"|from stdin",
        "Executable wrapper lost argv, stdio, or child exit: " + error + output);
    var state = new StateStore(fixture.StateDirectory);
    var persisted = state.LoadSession(state.SessionIdsForTest().Single());
    Check(persisted.ChildExitCode == 17 && persisted.FinalScanCompletedUtc is not null &&
          persisted.ConsoleControlMode == "redirected_input_no_keyboard_control" &&
          persisted.Status == "complete",
        "Redirected wrapper lost final-scan or child-exit evidence.");
    Console.WriteLine("EXIT_TRIPLE child=17 wrapper_process=17 test_executor=pass; console=redirected_input_no_keyboard_control");
}

static void CmdWrapper()
{
    using var fixture = SyntheticFixture.Create();
    string script = Path.Combine(fixture.Root, "script with space.cmd");
    File.WriteAllText(script, "@echo off\r\necho %~1^|%~2>cmd-result.txt\r\nexit /b 23\r\n");
    var command = new ProcessStartInfo(script);
    command.ArgumentList.Add("space value");
    command.ArgumentList.Add("second value");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(command);
    Check(run.BlastStatus == "ok" && run.ChildExitCode == 23 &&
          File.Exists(Path.Combine(fixture.Root, "cmd-result.txt")) &&
          File.ReadAllText(Path.Combine(fixture.Root, "cmd-result.txt")).Trim() ==
            "space value|second value",
        "Cmd wrapper did not preserve arguments or exit code: " + run.BlastStatus + "/" + run.ChildExitCode +
        "; actual=" + (File.Exists(Path.Combine(fixture.Root, "cmd-result.txt"))
            ? File.ReadAllText(Path.Combine(fixture.Root, "cmd-result.txt")) : "<missing>"));
}

static void CmdQuoteRejected()
{
    using var fixture = SyntheticFixture.Create();
    string script = Path.Combine(fixture.Root, "reject.cmd");
    File.WriteAllText(script, "@echo off\r\necho ran>ran.signal\r\n");
    var command = new ProcessStartInfo(script);
    command.ArgumentList.Add("quoted \"value\"");
    var engine = new SessionEngine(fixture);
    Throws<NotSupportedException>(() => engine.Run(command));
    Check(!File.Exists(Path.Combine(fixture.Root, "ran.signal")),
        "Unsupported cmd argument launched the child.");
}

static void CmdMetacharactersRejected()
{
    using var fixture = SyntheticFixture.Create();
    string script = Path.Combine(fixture.Root, "reject-metacharacters.cmd");
    File.WriteAllText(script, "@echo off\r\necho ran>ran.signal\r\n");
    var engine = new SessionEngine(fixture);
    foreach (string value in new[] { "%PATH%", "!VAR!", "x^y", "x&y", "x|y", "x<y", "x>y", "x\ny" })
    {
        var command = new ProcessStartInfo(script);
        command.ArgumentList.Add(value);
        Throws<NotSupportedException>(() => engine.Run(command));
        Check(!File.Exists(Path.Combine(fixture.Root, "ran.signal")),
            "Rejected cmd metacharacter launched the child.");
    }
}

static void CmdRawArgumentsRejected()
{
    using var fixture = SyntheticFixture.Create();
    string script = Path.Combine(fixture.Root, "reject-raw.cmd");
    File.WriteAllText(script, "@echo off\r\necho ran>ran.signal\r\n");
    var command = new ProcessStartInfo(script) { Arguments = "safe" };
    var engine = new SessionEngine(fixture);
    Throws<NotSupportedException>(() => engine.Run(command));
    Check(!File.Exists(Path.Combine(fixture.Root, "ran.signal")),
        "Raw cmd argument bypassed the checked ArgumentList path.");
}

static void CancelDuringBaseline()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    using var cancellation = new CancellationTokenSource();
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "before_baseline") cancellation.Cancel();
    };
    var run = engine.Run(Child(fixture, "mark"), cancellation.Token);
    var saved = engine.Report(run.SessionId);
    Check(run.ChildExitCode is null && run.BlastStatus == "interrupted" &&
          run.WrapperExitCode == 70 && run.CancelRequests == 1 &&
          saved.Status == "interrupted" && saved.Coverage != "complete" &&
          !File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
        "Baseline cancellation launched a child or left a ready session.");
}

static void CancelDuringFinalScan()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    using var cancellation = new CancellationTokenSource();
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "before_final_scan") cancellation.Cancel();
    };
    var run = engine.Run(Child(fixture, "modify"), cancellation.Token);
    var saved = engine.Report(run.SessionId);
    Check(run.ChildExitCode == 0 && run.BlastStatus == "interrupted" &&
          run.WrapperExitCode == 70 && run.CancelRequests == 1 &&
          saved.ChildExitCode == 0 && saved.Status == "interrupted" &&
          saved.Coverage != "complete" &&
          File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
        "Final-scan cancellation was reported as complete or lost the child exit code.");
}

static void CancelThenFinalizationFailure()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    using var cancellation = new CancellationTokenSource();
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "child_started") cancellation.Cancel();
        if (stage == "before_final_scan") throw new IOException("Synthetic final-scan failure after cancellation.");
    };
    var run = engine.Run(Child(fixture, "modify"), cancellation.Token);
    var saved = engine.Report(run.SessionId);
    Check(run.BlastStatus == "final_scan_failed" && run.ChildExitCode == 0 &&
          run.WrapperExitCode == 70 && run.CancelRequests == 1 &&
          saved.Status == "incomplete" && saved.Coverage == "incomplete" &&
          saved.ChildExitCode == 0 && saved.CoverageDetail.ScanFailure!.Contains("Synthetic final-scan failure"),
        "Failed finalization after cancellation was reported as success or lost the child exit code.");
}

static void UnresponsiveChildCancellation()
{
    using var fixture = SyntheticFixture.Create();
    using var cancellation = new CancellationTokenSource();
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "child_started") { cancellation.Cancel(); cancellation.Cancel(); }
    };
    var elapsed = Stopwatch.StartNew();
    try
    {
        var run = engine.Run(Child(fixture, "hang"), cancellation.Token);
        elapsed.Stop();
        var saved = engine.Report(run.SessionId);
        Check(run.BlastStatus == "interrupted" && run.WrapperExitCode == 70 &&
              run.ChildExitCode is null && run.ChildProcessId is not null &&
              run.CancelRequests == 1 && elapsed.Elapsed < TimeSpan.FromSeconds(10) &&
              saved.Status == "interrupted" && saved.Coverage == "incomplete" &&
              saved.InterruptionReason == "child_did_not_exit_within_5s_after_cancel" &&
              saved.Final is null && saved.FinalScanCompletedUtc is null,
            "Unresponsive direct child did not produce a bounded, diagnosable incomplete result.");
    }
    finally
    {
        string marker = Path.Combine(fixture.StateDirectory, "child-started.signal");
        if (File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out int ownedPid))
        {
            using var ownedChild = Process.GetProcessById(ownedPid);
            if (!ownedChild.HasExited) { ownedChild.Kill(); ownedChild.WaitForExit(10000); }
        }
    }
}

static void TerminatedWrapperAudit()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add("run-worker");
    start.ArgumentList.Add(fixture.DirectoryPath);
    start.ArgumentList.Add(fixture.WorkerToken);
    using var wrapper = Process.Start(start) ?? throw new IOException("Run worker did not start.");
    try
    {
        string marker = Path.Combine(fixture.StateDirectory, "wrapper-running.signal");
        Check(SpinWait.SpinUntil(() => File.Exists(marker), TimeSpan.FromSeconds(10)),
            "Run worker did not reach the durable running state.");
        string childMarker = Path.Combine(fixture.StateDirectory, "child-started.signal");
        Check(SpinWait.SpinUntil(() => File.Exists(childMarker), TimeSpan.FromSeconds(10)),
            "Synthetic direct child did not record its PID.");
        wrapper.Kill();
        Check(wrapper.WaitForExit(10000) && wrapper.ExitCode != 0,
            "Synthetic wrapper termination did not occur.");
        var state = new StateStore(fixture.StateDirectory);
        using (state.AcquireLock()) state.AuditUnresolved();
        var saved = state.LoadSession(state.SessionIdsForTest().Single());
        Check(saved.Status == "interrupted" && saved.Coverage == "incomplete" &&
              saved.ChildExitCode is null && saved.ChildProcessId is not null &&
              saved.InterruptionReason == "unfinished_on_restart_from_running" &&
              saved.CoverageDetail.ScanFailure!.Contains("background writer may still be running"),
            "Killed wrapper was reported as a complete protected session.");
    }
    finally
    {
        if (!wrapper.HasExited) { wrapper.Kill(); wrapper.WaitForExit(10000); }
        string childMarker = Path.Combine(fixture.StateDirectory, "child-started.signal");
        if (File.Exists(childMarker) && int.TryParse(File.ReadAllText(childMarker), out int ownedPid))
        {
            using var ownedChild = Process.GetProcessById(ownedPid);
            if (!ownedChild.HasExited) { ownedChild.Kill(); ownedChild.WaitForExit(10000); }
        }
    }
}

static void FailedFinalCommit()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var engine = new SessionEngine(fixture);
    int failures = 0;
    StateStore.BeforeSessionSaveForTest = session =>
    {
        if (session.Status == "complete" && Interlocked.Increment(ref failures) == 1)
            throw new IOException("Injected complete-session commit failure.");
    };
    try
    {
        var run = engine.Run(Child(fixture, "modify"));
        var saved = engine.Report(run.SessionId);
        Check(failures == 1 && run.BlastStatus == "final_scan_failed" &&
              run.ChildExitCode == 0 && run.WrapperExitCode == 70 &&
              saved.Status == "incomplete" && saved.Coverage == "incomplete" &&
              saved.ChildExitCode == 0 &&
              saved.CoverageDetail.ScanFailure!.Contains("commit failure"),
            "Failed final commit was reported as successful completion.");
    }
    finally { StateStore.BeforeSessionSaveForTest = null; }
}

static void PersistentFinalSaveFailure()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var engine = new SessionEngine(fixture);
    StateStore.BeforeSessionSaveForTest = session =>
    {
        if (session.Status is "complete" or "incomplete")
            throw new IOException("Injected persistent final persistence failure.");
    };
    RunResult result;
    try { result = engine.Run(Child(fixture, "modify")); }
    finally { StateStore.BeforeSessionSaveForTest = null; }
    Check(result.BlastStatus == "run_failed" && result.WrapperExitCode == 70 &&
          result.ChildExitCode == 0 && !result.DiagnosticPersisted &&
          result.Error!.Contains("persistent final persistence failure"),
        "Persistent final save failure escaped the shared error result or lost the observed child exit.");
    using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
    var saved = engine.State.LoadSession(engine.State.SessionIdsForTest().Single());
    Check(saved.Status == "interrupted" && saved.Coverage == "incomplete" &&
          saved.ChildExitCode == 0 && saved.InterruptionReason == "unfinished_on_restart_from_finalizing",
        "Persistent final-save failure left a falsely complete session or lost child exit evidence.");
}

static void BlastFailureProcessExit()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("failure-wrapper");
    start.ArgumentList.Add(fixture.DirectoryPath);
    start.ArgumentList.Add(fixture.WorkerToken);
    using var wrapper = Process.Start(start) ?? throw new IOException("Failure wrapper did not start.");
    string output = wrapper.StandardOutput.ReadToEnd();
    string error = wrapper.StandardError.ReadToEnd();
    Check(wrapper.WaitForExit(10000), "Failure wrapper did not exit.");
    var state = new StateStore(fixture.StateDirectory);
    var saved = state.LoadSession(state.SessionIdsForTest().Single());
    Check(output.Contains("FAILURE_WRAPPER child=23 blast=final_scan_failed") &&
          wrapper.ExitCode == 70 && saved.ChildExitCode == 23 &&
          saved.Status == "incomplete" && saved.FinalScanCompletedUtc is null,
        "Blast failure did not override wrapper exit while preserving child code: " + error + output);
    Console.WriteLine("EXIT_TRIPLE child=23 wrapper_process=70 test_executor=pass; blast=final_scan_failed");
}

static (RestorePlan Plan, string Target) ExistingPendingPlan(SyntheticFixture fixture, SessionEngine engine)
{
    string target = Path.Combine(fixture.Root, "alpha.txt");
    File.WriteAllText(target, "before");
    var first = engine.Run(Child(fixture, "modify"));
    Check(first.BlastStatus == "ok" && first.ChildExitCode == 0 && File.ReadAllText(target) == "after",
        "First synthetic session did not finish before the uncertain-child test.");
    var report = engine.Report(first.SessionId);
    return (engine.Preview(first.SessionId, [OnlyChange(report, ChangeKind.Modified).Id]), target);
}

static void AssertBlockedMutations(SessionEngine engine, SyntheticFixture fixture, RestorePlan plan, string target)
{
    bool runBlocked = false, applyBlocked = false;
    try { engine.Run(Child(fixture, "mark-next")); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Unresolved run", StringComparison.Ordinal))
    { runBlocked = true; }
    try { engine.Apply(plan.Id, plan.Hash); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Unresolved run", StringComparison.Ordinal))
    { applyBlocked = true; }
    using var report = JsonDocument.Parse(engine.ReportJson(plan.SessionId));
    bool reportBlocked = report.RootElement.GetProperty("store_modification_blocked").GetBoolean() &&
        report.RootElement.GetProperty("changes")[0].GetProperty("apply_eligibility").GetString() ==
        "blocked_unresolved_store_state";
    Check(runBlocked && applyBlocked &&
          reportBlocked &&
          !File.Exists(Path.Combine(fixture.StateDirectory, "next-child-started.signal")) &&
          File.ReadAllText(target) == "after",
        $"Unconfirmed child allowed mutation: runBlocked={runBlocked}, applyBlocked={applyBlocked}, " +
        $"target={File.ReadAllText(target)}");
    Console.WriteLine("BLOCK_EVIDENCE new_child_started=false apply_operations=0 target=after");
}

static Process OwnedHangChild(SyntheticFixture fixture, int? expectedPid = null)
{
    string marker = Path.Combine(fixture.StateDirectory, "child-started.signal");
    Check(SpinWait.SpinUntil(() => File.Exists(marker) &&
        int.TryParse(File.ReadAllText(marker), out _), TimeSpan.FromSeconds(10)),
        "Synthetic direct child did not publish its PID.");
    int pid = int.Parse(File.ReadAllText(marker));
    Check(expectedPid is null || expectedPid == pid, "Child PID differs from the observed Run result.");
    var process = Process.GetProcessById(pid);
    Check(!process.HasExited, "Synthetic direct child already exited before the barrier test.");
    return process;
}

static void StopOwnedHangChild(Process process, int ownedPid, DateTime startTime)
{
    try
    {
        if (process.Id == ownedPid && !process.HasExited && process.StartTime == startTime)
        { process.Kill(); Check(process.WaitForExit(10000), "Owned synthetic child did not exit after test cleanup."); }
    }
    finally { process.Dispose(); }
}

static void UnconfirmedChildBlocksMutations()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    var (plan, target) = ExistingPendingPlan(fixture, engine);
    using var cancellation = new CancellationTokenSource();
    engine.BoundaryForTest = stage => { if (stage == "child_started") cancellation.Cancel(); };
    var second = engine.Run(Child(fixture, "hang"), cancellation.Token);
    engine.BoundaryForTest = null;
    var child = OwnedHangChild(fixture, second.ChildProcessId);
    int pid = child.Id;
    DateTime started = child.StartTime;
    try
    {
        Check(second.BlastStatus == "interrupted" && second.ChildExitCode is null && !child.HasExited,
            "Cancelled direct child was not observed alive after Run returned.");
        AssertBlockedMutations(engine, fixture, plan, target);
    }
    finally { StopOwnedHangChild(child, pid, started); }
}

static void TerminatedWrapperBlocksMutations()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    var (plan, target) = ExistingPendingPlan(fixture, engine);
    using var wrapper = Worker("run-worker", fixture);
    Process? child = null;
    try
    {
        Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(fixture.StateDirectory,
            "wrapper-running.signal")), TimeSpan.FromSeconds(10)), "Wrapper did not reach durable running.");
        child = OwnedHangChild(fixture);
        wrapper.Kill();
        Check(wrapper.WaitForExit(10000), "Test wrapper did not terminate.");
        using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
        Check(!child.HasExited, "Direct child exited before post-audit mutation test.");
        AssertBlockedMutations(engine, fixture, plan, target);
    }
    finally
    {
        if (!wrapper.HasExited) { wrapper.Kill(); wrapper.WaitForExit(10000); }
        if (child is not null) StopOwnedHangChild(child, child.Id, child.StartTime);
    }
}

static void RunningSaveFailureBlocksMutations()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    var (plan, target) = ExistingPendingPlan(fixture, engine);
    StateStore.BeforeSessionSaveForTest = session =>
    {
        if (session.Status == "running") throw new IOException("Synthetic running-state write failure.");
    };
    RunResult second;
    try { second = engine.Run(Child(fixture, "hang")); }
    finally { StateStore.BeforeSessionSaveForTest = null; }
    var child = OwnedHangChild(fixture, second.ChildProcessId);
    int pid = child.Id;
    DateTime started = child.StartTime;
    try
    {
        Check(second.WrapperExitCode == 70 && second.ChildExitCode is null && !child.HasExited,
            "Running-state save failure did not preserve uncertain child result.");
        AssertBlockedMutations(engine, fixture, plan, target);
    }
    finally { StopOwnedHangChild(child, pid, started); }
}

static void PrelaunchDurableFailurePreventsStart()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    int failures = 0;
    StateStore.BeforeSessionSaveForTest = session =>
    {
        if (session.Status == "launch_pending")
        { failures++; throw new IOException("Synthetic prelaunch durable failure."); }
    };
    RunResult first;
    try { first = engine.Run(Child(fixture, "mark")); }
    finally { StateStore.BeforeSessionSaveForTest = null; }
    Check(failures == 1 && first.WrapperExitCode == 70 && first.ChildProcessId is null &&
          !File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
        "Command started without a durable auditable prelaunch state.");
    var second = engine.Run(Child(fixture, "mark-next"));
    Check(second.BlastStatus == "ok" &&
          File.Exists(Path.Combine(fixture.StateDirectory, "next-child-started.signal")),
        "Confirmed prelaunch failure incorrectly blocked a later run.");
}

static void BaselineCancellationPermitsLaterRun()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    using var cancellation = new CancellationTokenSource();
    engine.BoundaryForTest = stage => { if (stage == "before_baseline") cancellation.Cancel(); };
    var first = engine.Run(Child(fixture, "mark"), cancellation.Token);
    engine.BoundaryForTest = null;
    var second = engine.Run(Child(fixture, "mark-next"));
    Check(first.BlastStatus == "interrupted" && first.ChildProcessId is null &&
          second.BlastStatus == "ok" && File.Exists(Path.Combine(fixture.StateDirectory,
              "next-child-started.signal")), "Baseline cancellation created a phantom live-child block.");
}

static void ConfirmedCompletionPermitsLaterRun()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    var first = engine.Run(Child(fixture, "mark"));
    var second = engine.Run(Child(fixture, "mark-next"));
    Check(first.BlastStatus == "ok" && second.BlastStatus == "ok" &&
          File.Exists(Path.Combine(fixture.StateDirectory, "next-child-started.signal")),
        "Confirmed completed direct child prevented a later run.");
}

static void RealSqliteDoubleFailure()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    var (plan, target) = ExistingPendingPlan(fixture, engine);
    var state = engine.State;
    using (var connection = new SqliteConnection($"Data Source={state.DatabasePathForTest};Pooling=False"))
    {
        connection.Open();
        using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER reject_terminal_session BEFORE UPDATE OF status ON sessions
            WHEN NEW.status IN ('complete','incomplete')
            BEGIN SELECT RAISE(ABORT, 'synthetic terminal SQLite write rejection'); END;
            """;
        trigger.ExecuteNonQuery();
    }
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("sqlite-failure-wrapper");
    start.ArgumentList.Add(fixture.DirectoryPath);
    start.ArgumentList.Add(fixture.WorkerToken);
    using var wrapper = Process.Start(start) ?? throw new IOException("SQLite failure wrapper did not start.");
    string output = wrapper.StandardOutput.ReadToEnd();
    string error = wrapper.StandardError.ReadToEnd();
    Check(wrapper.WaitForExit(10000), "SQLite failure wrapper did not exit.");
    Check(wrapper.ExitCode == 70 && output.Contains("child=23") &&
          output.Contains("diagnostic_persisted=False") &&
          output.Contains("terminal_save_attempts=2") && output.Contains("SqliteException"),
        "Actual SQLite double failure did not reach shared error result: " + error + output);
    using (var connection = new SqliteConnection($"Data Source={state.DatabasePathForTest};Pooling=False"))
    {
        connection.Open();
        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TRIGGER reject_terminal_session;";
        drop.ExecuteNonQuery();
    }
    using (state.AcquireLock()) state.AuditUnresolved();
    var session = state.SessionIdsForTest().Select(state.LoadSession).Single(x => x.ChildExitCode == 23);
    Check(session.Status == "interrupted" && session.ChildExitCode == 23 &&
          session.InterruptionReason == "unfinished_on_restart_from_finalizing" &&
          state.HasUnresolvedRuns(), "SQLite recovery audit lost incomplete result.");
    AssertBlockedMutations(engine, fixture, plan, target);
    Console.WriteLine("EXIT_TRIPLE child=23 wrapper_process=70 test_executor=pass; diagnostic_persisted=false");
}

static void AbnormalChildExit()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("failfast-child");
    var run = engine.Run(command);
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "ok" && run.ChildExitCode is not null && run.ChildExitCode != 0 &&
          report.ChildExitCode == run.ChildExitCode && report.Status == "complete",
        "Abnormal child exit was hidden or confused with a Blast failure.");
}

static void MissingSafetyAfterRestart()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string id = OnlyChange(report, ChangeKind.Modified).Id;
        var plan = engine.Preview(report.Id, [id]);
        using var worker = Worker("apply-worker", fixture, plan.Id, plan.Hash, "crash-intent_durable");
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Intent crash did not occur.");
        var beforeAudit = engine.State.LoadPlan(plan.Id);
        string safety = beforeAudit.Operations.Single().SafetyObjectId!;
        File.Delete(engine.Objects.PathForTest(safety));
        using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
        var audited = engine.State.LoadPlan(plan.Id);
        Check(audited.Operations.Single().Message!.Contains("safety=invalid"),
            "Audit did not report missing safety object.");
        var second = engine.Preview(report.Id, [id]);
        Throws<InvalidOperationException>(() => engine.Apply(second.Id, second.Hash));
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "A missing safety object allowed target mutation.");
    }
}

static void ParentMoveAtIntent()
{
    using var fixture = SyntheticFixture.Create();
    string parent = Path.Combine(fixture.Root, "nested");
    Directory.CreateDirectory(parent);
    File.WriteAllText(Path.Combine(parent, "item.bin"), "before");
    var engine = new SessionEngine(fixture);
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("mutate-nested");
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    var run = engine.Run(command);
    var change = OnlyChange(engine.Report(run.SessionId), ChangeKind.Modified);
    var plan = engine.Preview(run.SessionId, [change.Id]);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "intent_durable") Directory.Move(parent, parent + "-moved");
    };
    var applied = engine.Apply(plan.Id, plan.Hash);
    Check(applied.OperationsApplied == 0 && applied.Status.Contains("in doubt") &&
          File.ReadAllText(Path.Combine(parent + "-moved", "item.bin")) == "after",
        "Moved parent was modified after durable intent.");
}

static void TargetRaceAtVerification()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        bool replacementSucceeded = false;
        engine.BoundaryForTest = stage =>
        {
            if (stage != "target_modified") return;
            string target = Path.Combine(fixture.Root, "alpha.txt");
            string other = Path.Combine(fixture.Root, "other.txt");
            File.WriteAllText(other, "attacker");
            try { File.Move(other, target, overwrite: true); replacementSucceeded = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
        var applied = engine.Apply(plan.Id, plan.Hash);
        Check(!replacementSucceeded && applied.Status == "verified" && applied.OperationsApplied == 1 &&
              File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "before",
            "Target replacement succeeded while executor verified a live handle.");
    }
}

static void CorruptBaselineBeforeReady()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage != "baseline_saved") return;
        string objectId = engine.State.LoadSession(engine.State.SessionIdsForTest().Single())
            .Baseline["alpha.txt"].ObjectId!;
        File.WriteAllBytes(engine.Objects.PathForTest(objectId), [1, 2, 3]);
    };
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "baseline_failed" && report.Status == "failed" &&
          !File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
        "Corrupt baseline object entered ready or launched child.");
}

static void OverwriteRenameRejected()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "alpha before");
    File.WriteAllText(Path.Combine(fixture.Root, "bravo.txt"), "bravo before");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "overwrite-rename"));
    var report = engine.Report(run.SessionId);
    Check(report.Changes?.Count == 1 && report.Changes[0].Kind == ChangeKind.Unsupported &&
          report.Changes[0].Path == "alpha.txt" && report.Changes[0].Destination == "bravo.txt" &&
          report.Changes[0].UnsupportedReason!.StartsWith("unsupported_to_apply"),
        "Overwrite rename was decomposed into recoverable file operations.");
    Throws<NotSupportedException>(() => engine.Preview(report.Id, [report.Changes![0].Id]));
    Check(!File.Exists(Path.Combine(fixture.Root, "alpha.txt")) &&
          File.ReadAllText(Path.Combine(fixture.Root, "bravo.txt")) == "alpha before",
        "Unsupported preview modified the target.");
}

static void CoverageBuckets()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "good.txt"), "complete");
    File.WriteAllText(Path.Combine(fixture.Root, ".env"), "excluded synthetic");
    File.WriteAllBytes(Path.Combine(fixture.Root, "large.bin"), new byte[FileSystemScope.MaxFileBytes + 1]);
    string lockedPath = Path.Combine(fixture.Root, "locked.txt");
    File.WriteAllText(lockedPath, "read failure");
    using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(report.RuleVersion == "synthetic-v2-explicit-git-env" &&
          report.CoverageDetail.ConfirmedExclusions.Contains(".env") &&
          report.CoverageDetail.ReadFailures.Contains("locked.txt") &&
          report.CoverageDetail.UnsupportedTypes.Contains("large.bin") &&
          report.CoverageDetail.CompleteBaselines.Contains("good.txt") &&
          report.Coverage == "failed" && !File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
        "Four coverage classes or fixed rule version were not persisted separately.");
}

static void OrdinaryCliClosed()
{
    using var fixture = SyntheticFixture.Create();
    var info = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, RedirectStandardError = true,
        RedirectStandardOutput = true
    };
    info.ArgumentList.Add(typeof(SessionEngine).Assembly.Location);
    info.ArgumentList.Add("run");
    info.ArgumentList.Add("--root");
    info.ArgumentList.Add(fixture.Root);
    using var process = Process.Start(info) ?? throw new IOException("CLI did not start.");
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Check(process.ExitCode == 4 && error.Contains("not enabled for user directories") &&
          !File.Exists(Path.Combine(fixture.StateDirectory, "state.db")),
        "Ordinary CLI entered a protected directory before the real-data gate.");
}

static void R1HardLinkAtIntent()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string target = Path.Combine(fixture.Root, "alpha.txt");
        string alias = Path.Combine(fixture.DirectoryPath, "unselected-alias.txt");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        engine.BoundaryForTest = stage =>
        {
            if (stage == "intent_durable") WindowsFiles.CreateHardLinkForTest(alias, target);
        };
        var applied = engine.Apply(plan.Id, plan.Hash);
        Check(applied.OperationsApplied == 0 && applied.Status.Contains("in doubt") &&
              File.ReadAllText(target) == "after" && File.ReadAllText(alias) == "after" &&
              new FileInfo(target).LinkTarget is null,
            "Recovery changed a now multi-linked target or alias.");
    }
}

static void R2ChangedTargetObject()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string id = OnlyChange(report, ChangeKind.Modified).Id;
        var plan = engine.Preview(report.Id, [id]);
        byte[] substituted = Encoding.UTF8.GetBytes("different baseline");
        string objectId = engine.Objects.Save(substituted);
        var changed = report.Changes!.Single(c => c.Id == id);
        report.Changes = [changed with
        {
            Baseline = changed.Baseline with
            {
                ObjectId = objectId, ContentHash = ObjectStore.Hash(substituted), Length = substituted.Length
            }
        }];
        engine.State.SaveSession(report);
        Throws<InvalidOperationException>(() => engine.Apply(plan.Id, plan.Hash));
        Check(engine.State.LoadPlan(plan.Id).Status == "planned" &&
              File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "Confirmed plan executed a newly substituted baseline object.");
    }
}

static void R2ChangedScope()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        report.Scopes[0] = report.Scopes[0] with { Kind = "external_directory" };
        engine.State.SaveSession(report);
        Throws<InvalidOperationException>(() => engine.Apply(plan.Id, plan.Hash));
        Check(engine.State.LoadPlan(plan.Id).Status == "planned" &&
              File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "Confirmed plan ignored a changed scope definition.");
    }
}

static void R3BaselineAds()
{
    using var fixture = SyntheticFixture.Create();
    string file = Path.Combine(fixture.Root, "alpha.txt");
    File.WriteAllText(file, "before");
    File.WriteAllText(file + ":fixture_stream", "fake ADS token");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "baseline_failed" &&
          report.CoverageDetail.UnsupportedTypes.Contains("alpha.txt") &&
          !File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
        "ADS was classified as a complete required baseline.");
}

static void R3AdsBeforeApply()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string file = Path.Combine(fixture.Root, "alpha.txt");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        engine.BoundaryForTest = stage =>
        {
            if (stage == "intent_durable") File.WriteAllText(file + ":fixture_stream", "fake ADS token");
        };
        var applied = engine.Apply(plan.Id, plan.Hash);
        Check(applied.OperationsApplied == 0 && File.ReadAllText(file) == "after" &&
              File.ReadAllText(file + ":fixture_stream") == "fake ADS token",
            "Recovery modified a file after an ADS appeared.");
    }
}

static void R4RecreatedRoot()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage != "before_final_scan") return;
        Directory.Move(fixture.Root, Path.Combine(fixture.DirectoryPath, "moved-work"));
        Directory.CreateDirectory(fixture.Root);
    };
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus != "ok" || report.Changes!.All(c => c.Kind == ChangeKind.Unsupported),
        "Recreated root produced an executable ordinary deletion.");
    Check(!File.Exists(Path.Combine(fixture.Root, "alpha.txt")), "Root-replacement test modified new root.");
}

static void R4RecreatedParent()
{
    using var fixture = SyntheticFixture.Create();
    string parent = Path.Combine(fixture.Root, "nested");
    Directory.CreateDirectory(parent);
    File.WriteAllText(Path.Combine(parent, "item.bin"), "before");
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage != "before_final_scan") return;
        Directory.Move(parent, Path.Combine(fixture.DirectoryPath, "moved-nested"));
        Directory.CreateDirectory(parent);
    };
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("mutate");
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    command.ArgumentList.Add("mark");
    var run = engine.Run(command);
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus != "ok" || report.Changes!.All(c => c.Kind == ChangeKind.Unsupported),
        "Recreated parent produced an executable ordinary deletion.");
    Check(!File.Exists(Path.Combine(parent, "item.bin")), "Parent-replacement test modified new directory.");
}

static void R4RootReparentAtIntent() => R4ReparentAtIntent(replaceRoot: true);

static void R4IntermediateReparentAtIntent() => R4ReparentAtIntent(replaceRoot: false);

static void R4OrdinaryNestedRestore()
{
    using var fixture = SyntheticFixture.Create();
    string parent = Path.Combine(fixture.Root, "nested");
    Directory.CreateDirectory(parent);
    string file = Path.Combine(parent, "item.txt");
    File.WriteAllText(file, "before");
    var engine = new SessionEngine(fixture);
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("mutate-relative");
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    command.ArgumentList.Add(Path.Combine("nested", "item.txt"));
    var run = engine.Run(command);
    var change = OnlyChange(engine.Report(run.SessionId), ChangeKind.Modified);
    var plan = engine.Preview(run.SessionId, [change.Id]);
    var result = engine.Apply(plan.Id, plan.Hash);
    Check(result.Status == "verified" && result.OperationsApplied == 1 && File.ReadAllText(file) == "before",
        $"Ordinary nested restoration failed: {result.Status}; {result.Operations.Single().Message}");
}

static void R4RootReparentOtherKind(ChangeKind kind)
{
    using var fixture = SyntheticFixture.Create();
    string parent = Path.Combine(fixture.Root, "nested");
    Directory.CreateDirectory(parent);
    string source = Path.Combine(parent, "item.txt");
    if (kind != ChangeKind.Added) File.WriteAllText(source, "before");
    FileIdentity oldRoot, oldParent;
    using (var handle = WindowsFiles.OpenDirectory(fixture.Root)) oldRoot = WindowsFiles.Identity(handle);
    using (var handle = WindowsFiles.OpenDirectory(parent)) oldParent = WindowsFiles.Identity(handle);
    var engine = new SessionEngine(fixture);
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("mutate-relative");
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    command.ArgumentList.Add(Path.Combine("nested", "item.txt"));
    command.ArgumentList.Add(kind switch
    {
        ChangeKind.Added => "add", ChangeKind.Deleted => "delete", ChangeKind.Renamed => "rename",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    });
    var run = engine.Run(command);
    Check(run.BlastStatus == "ok", "Fixture mutation did not finish.");
    var change = OnlyChange(engine.Report(run.SessionId), kind);
    var plan = engine.Preview(run.SessionId, [change.Id]);
    string current = kind == ChangeKind.Renamed ? Path.Combine(parent, "renamed.txt") : source;
    FileIdentity? currentIdentity = null;
    if (kind != ChangeKind.Deleted)
        using (var handle = WindowsFiles.OpenFile(current, write: false))
            currentIdentity = WindowsFiles.Identity(handle);
    engine.BoundaryForTest = stage =>
    {
        if (stage != "intent_durable") return;
        string oldWork = Path.Combine(fixture.DirectoryPath, "old-work");
        Directory.Move(fixture.Root, oldWork);
        Directory.CreateDirectory(fixture.Root);
        Directory.Move(Path.Combine(oldWork, "nested"), parent);
        FileIdentity rootNow, parentNow;
        using (var handle = WindowsFiles.OpenDirectory(fixture.Root)) rootNow = WindowsFiles.Identity(handle);
        using (var handle = WindowsFiles.OpenDirectory(parent)) parentNow = WindowsFiles.Identity(handle);
        Check(rootNow.Index != oldRoot.Index && parentNow == oldParent,
            "Root reparent identity precondition was not established.");
        if (currentIdentity is FileIdentity expected)
        {
            using var handle = WindowsFiles.OpenFile(current, write: false);
            Check(WindowsFiles.Identity(handle) == expected, "Current file identity changed in fixture.");
        }
        else Check(!File.Exists(current), "Deleted target unexpectedly exists in fixture.");
    };
    var result = engine.Apply(plan.Id, plan.Hash);
    Check(result.OperationsApplied == 0 && result.Operations.Single().Status != "verified" &&
          result.Operations.Single().Message!.Contains("directory identity", StringComparison.OrdinalIgnoreCase) &&
          (kind == ChangeKind.Deleted ? !File.Exists(current) :
           File.ReadAllText(current) == (kind == ChangeKind.Added ? "after" : "before")),
        $"Reparented root allowed {kind} recovery: {result.Status}; {result.Operations.Single().Message}");
}

static void ThreeNameCycleRejected()
{
    using var fixture = SyntheticFixture.Create();
    foreach (string name in new[] { "alpha", "bravo", "charlie" })
        File.WriteAllText(Path.Combine(fixture.Root, name + ".txt"), name);
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "cycle-three"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "ok" && report.Changes?.Count == 3 &&
          report.Changes.All(c => c.Kind == ChangeKind.Unsupported),
        "Three-name identity cycle was split into executable changes.");
    Throws<NotSupportedException>(() => engine.Preview(run.SessionId, report.Changes!.Select(c => c.Id)));
    Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "bravo" &&
          File.ReadAllText(Path.Combine(fixture.Root, "bravo.txt")) == "charlie" &&
          File.ReadAllText(Path.Combine(fixture.Root, "charlie.txt")) == "alpha",
        "Rejected cycle changed current files.");
}

static void CrossParentRenameRejected()
{
    using var fixture = SyntheticFixture.Create();
    string first = Path.Combine(fixture.Root, "nested");
    string second = Path.Combine(fixture.Root, "other");
    Directory.CreateDirectory(first);
    Directory.CreateDirectory(second);
    File.WriteAllText(Path.Combine(first, "item.txt"), "before");
    var engine = new SessionEngine(fixture);
    var run = engine.Run(Child(fixture, "cross-parent-rename"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "ok" && report.Changes?.Count == 1 &&
          report.Changes[0].Kind == ChangeKind.Unsupported,
        "Cross-parent move became executable changes.");
    Throws<NotSupportedException>(() => engine.Preview(run.SessionId, [report.Changes![0].Id]));
    Check(!File.Exists(Path.Combine(first, "item.txt")) &&
          File.ReadAllText(Path.Combine(second, "item.txt")) == "before",
        "Rejected cross-parent move changed current files.");
}

static void R4ReparentAtIntent(bool replaceRoot)
{
    using var fixture = SyntheticFixture.Create();
    string ancestor = replaceRoot ? fixture.Root : Path.Combine(fixture.Root, "middle");
    string parent = Path.Combine(ancestor, "nested");
    Directory.CreateDirectory(parent);
    string file = Path.Combine(parent, "item.txt");
    File.WriteAllText(file, "before");
    FileIdentity originalAncestor, originalParent, originalFile;
    using (var handle = WindowsFiles.OpenDirectory(ancestor)) originalAncestor = WindowsFiles.Identity(handle);
    using (var handle = WindowsFiles.OpenDirectory(parent)) originalParent = WindowsFiles.Identity(handle);
    using (var handle = WindowsFiles.OpenFile(file, write: false)) originalFile = WindowsFiles.Identity(handle);

    var engine = new SessionEngine(fixture);
    var command = new ProcessStartInfo(Environment.ProcessPath!);
    command.ArgumentList.Add("mutate-relative");
    command.ArgumentList.Add(fixture.DirectoryPath);
    command.ArgumentList.Add(fixture.WorkerToken);
    command.ArgumentList.Add(replaceRoot ? Path.Combine("nested", "item.txt") : Path.Combine("middle", "nested", "item.txt"));
    var run = engine.Run(command);
    Check(run.BlastStatus == "ok", "Nested fixture run did not finish.");
    var change = OnlyChange(engine.Report(run.SessionId), ChangeKind.Modified);
    var plan = engine.Preview(run.SessionId, [change.Id]);
    bool rearranged = false;
    engine.BoundaryForTest = stage =>
    {
        if (stage != "intent_durable") return;
        string oldAncestor = Path.Combine(fixture.DirectoryPath, replaceRoot ? "old-work" : "old-middle");
        Directory.Move(ancestor, oldAncestor);
        Directory.CreateDirectory(ancestor);
        string restoredParent = Path.Combine(ancestor, "nested");
        Directory.Move(Path.Combine(oldAncestor, "nested"), restoredParent);
        FileIdentity newAncestor, sameParent, sameFile;
        using (var handle = WindowsFiles.OpenDirectory(ancestor)) newAncestor = WindowsFiles.Identity(handle);
        using (var handle = WindowsFiles.OpenDirectory(restoredParent)) sameParent = WindowsFiles.Identity(handle);
        using (var handle = WindowsFiles.OpenFile(Path.Combine(restoredParent, "item.txt"), write: false))
            sameFile = WindowsFiles.Identity(handle);
        Check(newAncestor.Index != originalAncestor.Index &&
              sameParent == originalParent && sameFile == originalFile &&
              File.ReadAllText(Path.Combine(restoredParent, "item.txt")) == "after",
            "The directory reparenting identity precondition was not established.");
        rearranged = true;
    };
    var result = engine.Apply(plan.Id, plan.Hash);
    Check(rearranged && result.OperationsApplied == 0 &&
          result.Operations.Single().Status != "verified" &&
          result.Operations.Single().Message!.Contains("directory identity", StringComparison.OrdinalIgnoreCase) &&
          File.ReadAllText(file) == "after",
        $"Reparented ancestor allowed restore or lacked a directory identity conflict: status={result.Status}, " +
        $"applied={result.OperationsApplied}, operation={result.Operations.Single().Status}, " +
        $"message={result.Operations.Single().Message}, content={File.ReadAllText(file)}.");
}

static void R5SameContentReplacement()
{
    var (fixture, engine, report) = Setup("before", "delete");
    using (fixture)
    {
        string target = Path.Combine(fixture.Root, "alpha.txt");
        string replacement = Path.Combine(fixture.DirectoryPath, "replacement.txt");
        string moved = Path.Combine(fixture.DirectoryPath, "created-moved.txt");
        File.WriteAllText(replacement, "before");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Deleted).Id]);
        bool attempted = false, blocked = false;
        engine.BoundaryForTest = stage =>
        {
            if (stage != "target_modified") return;
            attempted = true;
            try
            {
                File.Move(target, moved);
                File.Move(replacement, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { blocked = true; }
        };
        var applied = engine.Apply(plan.Id, plan.Hash);
        ulong actualId;
        using (var handle = WindowsFiles.OpenFile(target, write: false))
            actualId = WindowsFiles.Identity(handle).Index;
        Check(attempted && blocked && applied.Status == "verified" &&
              applied.OperationsApplied == 1 && applied.Operations.Single().ActualFileId == actualId &&
              File.ReadAllText(target) == "before" && File.Exists(replacement),
            "Replacement bypassed the live created handle or result identity was incorrect.");
    }
}

static void R1AttributesAtIntent()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string target = Path.Combine(fixture.Root, "alpha.txt");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        engine.BoundaryForTest = stage =>
        {
            if (stage == "intent_durable") File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.Hidden);
        };
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && File.ReadAllText(target) == "after",
            "Final opened handle ignored changed file attributes.");
    }
}

static void R1AddedHardLink() => R1LinkedChange("<absent>", "add", ChangeKind.Added);

static void R1RenamedHardLink() => R1LinkedChange("before", "rename", ChangeKind.Renamed);

static void R1LinkedChange(string initial, string mutation, ChangeKind kind)
{
    var (fixture, engine, report) = Setup(initial, mutation);
    using (fixture)
    {
        string current = Path.Combine(fixture.Root, kind == ChangeKind.Added ? "new.txt" : "beta.txt");
        string alias = Path.Combine(fixture.DirectoryPath, "unselected-alias.txt");
        var plan = engine.Preview(report.Id, [OnlyChange(report, kind).Id]);
        engine.BoundaryForTest = stage =>
        {
            if (stage == "intent_durable") WindowsFiles.CreateHardLinkForTest(alias, current);
        };
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && File.Exists(current) && File.Exists(alias) &&
              File.ReadAllText(current) == File.ReadAllText(alias),
            "Linked file was removed or renamed after final handle check.");
    }
}

static void R1CaptureSymlinkSwap()
{
    using var fixture = SyntheticFixture.Create();
    string target = Path.Combine(fixture.Root, "alpha.txt");
    string outside = Path.Combine(fixture.DirectoryPath, "outside.txt");
    File.WriteAllText(target, "ordinary");
    string fakeToken = "FAKE_OUTSIDE_" + Guid.NewGuid().ToString("N");
    File.WriteAllText(outside, fakeToken);
    bool swapped = false;
    FileSystemScope.AfterPathAttributesForTest = full =>
    {
        if (swapped || !full.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(target);
        try { File.CreateSymbolicLink(target, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            File.WriteAllText(target, "ordinary");
            throw new TestBlockedException("Non-elevated symlink creation unavailable: " +
                ex.GetType().Name + "; HResult=0x" + ex.HResult.ToString("X8") +
                "; message=" + ex.Message +
                (ex.InnerException is null ? "" : "; inner=" + ex.InnerException));
        }
        swapped = true;
    };
    try
    {
        var engine = new SessionEngine(fixture);
        var captured = FileSystemScope.Capture(fixture.Root, "alpha.txt", engine.Objects);
        Check(swapped && captured.State.Presence == Presence.Unknown &&
              !File.Exists(engine.Objects.PathForTest(ObjectStore.Hash(Encoding.UTF8.GetBytes(fakeToken)))),
            "Capture followed a leaf symlink created after path attributes were read.");
    }
    finally { FileSystemScope.AfterPathAttributesForTest = null; }
}

static void R1CaptureHardLinkSwap()
{
    using var fixture = SyntheticFixture.Create();
    string target = Path.Combine(fixture.Root, "alpha.txt");
    string outside = Path.Combine(fixture.DirectoryPath, "outside.txt");
    File.WriteAllText(target, "ordinary");
    string fakeToken = "FAKE_OUTSIDE_" + Guid.NewGuid().ToString("N");
    File.WriteAllText(outside, fakeToken);
    bool swapped = false;
    FileSystemScope.AfterPathAttributesForTest = full =>
    {
        if (swapped || !full.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(target);
        WindowsFiles.CreateHardLinkForTest(target, outside);
        swapped = true;
    };
    try
    {
        var engine = new SessionEngine(fixture);
        var captured = FileSystemScope.Capture(fixture.Root, "alpha.txt", engine.Objects);
        Check(swapped && captured.State.Presence == Presence.Unknown &&
              !File.Exists(engine.Objects.PathForTest(ObjectStore.Hash(Encoding.UTF8.GetBytes(fakeToken)))),
            "Capture saved content reached through an unselected hard-link alias.");
    }
    finally { FileSystemScope.AfterPathAttributesForTest = null; }
}

static void R2ChangedOperationKind()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var original = OnlyChange(report, ChangeKind.Modified);
        var plan = engine.Preview(report.Id, [original.Id]);
        report.Changes = [original with { Kind = ChangeKind.Added }];
        engine.State.SaveSession(report);
        Throws<InvalidOperationException>(() => engine.Apply(plan.Id, plan.Hash));
        Check(File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after" &&
              engine.State.LoadPlan(plan.Id).Status == "planned",
            "Changed operation kind was executed under old confirmation.");
    }
}

static void R2InterruptedAuditConfirmedPath()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var original = OnlyChange(report, ChangeKind.Modified);
        var plan = engine.Preview(report.Id, [original.Id]);
        using var worker = Worker("apply-worker", fixture, plan.Id, plan.Hash, "crash-intent_durable");
        Check(worker.WaitForExit(10000) && worker.ExitCode != 0, "Audit fixture did not crash at intent.");
        report.Changes = [original with { Path = "unselected.txt" }];
        engine.State.SaveSession(report);
        using (engine.State.AcquireLock()) engine.State.AuditUnresolved();
        var operation = engine.State.LoadPlan(plan.Id).Operations.Single();
        Check(operation.Status == "in_doubt" && operation.ActualPresence == Presence.Present &&
              operation.ActualContentHash == ObjectStore.Hash(Encoding.UTF8.GetBytes("after")),
            "Interrupted audit followed a mutable session path instead of the confirmed plan.");
    }
}

static void R3AddedAds()
{
    var (fixture, engine, report) = Setup("<absent>", "add");
    using (fixture)
    {
        string target = Path.Combine(fixture.Root, "new.txt");
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Added).Id]);
        engine.BoundaryForTest = stage =>
        {
            if (stage == "intent_durable") File.WriteAllText(target + ":fixture_stream", "fake ADS token");
        };
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && File.ReadAllText(target) == "new content" &&
              File.ReadAllText(target + ":fixture_stream") == "fake ADS token",
            "Added file with ADS was removed or safety copy was incomplete.");
    }
}

static void R3StreamQueryFailure()
{
    using var fixture = SyntheticFixture.Create();
    File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "before");
    WindowsFiles.BeforeStreamQueryForTest = () => throw new IOException("Injected stream query failure.");
    try
    {
        var engine = new SessionEngine(fixture);
        var run = engine.Run(Child(fixture, "mark"));
        var report = engine.Report(run.SessionId);
        Check(run.BlastStatus == "baseline_failed" &&
              report.Baseline["alpha.txt"].Presence == Presence.Unknown &&
              report.CoverageDetail.ReadFailures.Contains("alpha.txt") &&
              !File.Exists(Path.Combine(fixture.StateDirectory, "child-started.signal")),
            "Stream query error was treated as absence of ADS.");
    }
    finally { WindowsFiles.BeforeStreamQueryForTest = null; }
}

static void R4ExternalDirectory()
{
    using var fixture = SyntheticFixture.Create();
    string external = Path.Combine(fixture.DirectoryPath, "outside-dir");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "chosen.txt"), "before");
    fixture.AddExternalDirectory(external);
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage != "before_final_scan") return;
        Directory.Move(external, Path.Combine(fixture.DirectoryPath, "outside-dir-moved"));
        Directory.CreateDirectory(external);
    };
    var run = engine.Run(Child(fixture, "mark"));
    Check(run.BlastStatus == "final_scan_incomplete" &&
          engine.Report(run.SessionId).Status == "incomplete" &&
          !File.Exists(Path.Combine(external, "chosen.txt")),
        "External directory replacement retained a usable baseline.");
}

static void R4EmptyDirectory()
{
    using var fixture = SyntheticFixture.Create();
    string empty = Path.Combine(fixture.Root, "empty");
    Directory.CreateDirectory(empty);
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage != "before_final_scan") return;
        Directory.Move(empty, Path.Combine(fixture.DirectoryPath, "old-empty"));
        Directory.CreateDirectory(empty);
    };
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(report.BaselineDirectories.ContainsKey("workspace|empty") &&
          run.BlastStatus == "final_scan_incomplete" && report.Status == "incomplete",
        "Empty directory identity was not retained across scans.");
}

static void R4NewDirectory()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage == "before_final_scan") Directory.CreateDirectory(Path.Combine(fixture.Root, "newdir"));
    };
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "final_scan_incomplete" && report.Status == "incomplete" &&
          report.FinalDirectories!.ContainsKey("workspace|newdir") &&
          !report.BaselineDirectories.ContainsKey("workspace|newdir"),
        "New directory was silently treated as an existing baseline parent.");
}

static void R4NewDirectoryFile()
{
    using var fixture = SyntheticFixture.Create();
    var engine = new SessionEngine(fixture);
    engine.BoundaryForTest = stage =>
    {
        if (stage != "before_final_scan") return;
        string added = Path.Combine(fixture.Root, "newdir");
        Directory.CreateDirectory(added);
        File.WriteAllText(Path.Combine(added, "item.txt"), "new content");
    };
    var run = engine.Run(Child(fixture, "mark"));
    var report = engine.Report(run.SessionId);
    Check(run.BlastStatus == "final_scan_incomplete" &&
          report.Baseline[Path.Combine("newdir", "item.txt")].Presence == Presence.Unknown &&
          report.Changes!.All(c => c.Kind == ChangeKind.Unsupported),
        "A new directory used its final parent ID as a historical absent baseline.");
}

static void R4RootChangedBeforeApply()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        var plan = engine.Preview(report.Id, [OnlyChange(report, ChangeKind.Modified).Id]);
        Directory.Move(fixture.Root, Path.Combine(fixture.DirectoryPath, "old-work"));
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, "alpha.txt"), "after");
        var result = engine.Apply(plan.Id, plan.Hash);
        Check(result.OperationsApplied == 0 && File.ReadAllText(Path.Combine(fixture.Root, "alpha.txt")) == "after",
            "Apply used a new root with the confirmed plan.");
    }
}

static void MissingObjectKey()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string key = Path.Combine(fixture.StateDirectory, "object-key.dpapi");
        string objectId = report.Baseline["alpha.txt"].ObjectId!;
        string objectPath = engine.Objects.PathForTest(objectId);
        Check(File.Exists(key) && File.Exists(objectPath), "Key-loss fixture was not established.");
        File.Delete(key);
        Throws<InvalidDataException>(() => _ = new SessionEngine(fixture));
        Check(!File.Exists(key) && File.Exists(objectPath) &&
              engine.State.LoadSession(report.Id).Status == "complete",
            "Missing key silently regenerated or removed existing evidence.");
    }
}

static void ReportEligibility()
{
    var (fixture, engine, report) = Setup("before", "modify");
    using (fixture)
    {
        string baselineObject = report.Baseline["alpha.txt"].ObjectId!;
        File.Delete(engine.Objects.PathForTest(baselineObject));
        using var json = System.Text.Json.JsonDocument.Parse(engine.ReportJson(report.Id));
        var change = json.RootElement.GetProperty("changes")[0];
        Check(change.GetProperty("operation_supported").GetBoolean() &&
              change.GetProperty("snapshot_integrity").GetString() == "missing_or_corrupt" &&
              change.GetProperty("apply_eligibility").GetString() == "unverified" &&
              !change.TryGetProperty("recoverable", out _),
            "Report overstated current recoverability after object loss.");
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

internal sealed class TestBlockedException(string message) : Exception(message);
