# Blast Radius

Blast Radius is an experimental Windows CLI for reviewing changes across a selected workspace and explicit external paths, then restoring selected files with conflict checks and a recovery journal. It uses C#/.NET 10, SQLite, and an encrypted content-addressed object store.

**Status: Stage 1 in progress.** Recovery is available only through test tooling that creates synthetic temporary fixtures. Ordinary user-directory `run`, `undo`, and `report` are disabled until the real-data gate in [Plan.md](Plan.md) is met. Do not use this version to protect real files.

## Build and test

On Windows with .NET SDK 10 and a local NTFS volume:

```powershell
dotnet restore BlastRadius.slnx --use-lock-file
dotnet build BlastRadius.slnx --configuration Debug --no-restore
dotnet run --no-build --project tests/Blast.Tests -- all
dotnet run --no-build --project tests/Blast.Tests -- demo
```

The current local test record is 120 passed, 0 failed, and 1 blocked. The blocked test needs permission to create a symbolic link; the test runner returns a nonzero exit code when any test is blocked. Synthetic terminal tests also exercise Ctrl+C through the shared command path. The synthetic demo restores one modified file. Permission and attribute support is limited to the tested Stage 1 subset; capture of a file does not imply every restore operation supports its attributes. See [acceptance evidence](evidence/acceptance-map.md) for individual results and remaining limits.

No Claude hooks, watcher, cross-volume recovery, or real-directory trial are part of this stage.
