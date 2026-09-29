namespace Blast;

// Only the test assembly can construct this capability. Production CLI paths cannot create one.
internal sealed class SyntheticFixture : IDisposable
{
    internal string DirectoryPath { get; }
    internal string Root { get; }
    internal string StateDirectory { get; }
    internal string WorkerToken { get; }
    internal List<ProtectionScope> Scopes { get; } = [];
    private readonly bool ownsDirectory;

    private SyntheticFixture(string directory, string token, bool create)
    {
        DirectoryPath = directory;
        Root = Path.Combine(directory, "work");
        StateDirectory = Path.Combine(directory, "state");
        WorkerToken = token;
        ownsDirectory = create;
        if (create)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(Path.Combine(directory, "fixture.token"), token);
        }
        Scopes.Add(new ProtectionScope("workspace", Root, "workspace"));
    }

    internal static SyntheticFixture Create()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows NTFS is required.");
        string volumeRoot = Path.GetPathRoot(Directory.GetCurrentDirectory())!;
        if (new DriveInfo(volumeRoot).DriveFormat != "NTFS")
            throw new PlatformNotSupportedException("The test working volume must be NTFS.");
        string directory = Path.Combine(volumeRoot, "BlastRadiusFixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { return new SyntheticFixture(directory, Guid.NewGuid().ToString("N"), create: true); }
        catch
        {
            Console.Error.WriteLine("FIXTURE_SETUP_FAILED path=" + directory);
            throw;
        }
    }

    internal static SyntheticFixture AttachWorker(string directory, string token)
    {
        string full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != Path.GetPathRoot(full) ||
            !Path.GetFileName(full).StartsWith("BlastRadiusFixture-", StringComparison.Ordinal))
            throw new InvalidOperationException("Worker target is not a generated fixture.");
        string expected = File.ReadAllText(Path.Combine(full, "fixture.token"));
        if (expected != token) throw new UnauthorizedAccessException("Invalid fixture worker token.");
        return new SyntheticFixture(full, token, create: false);
    }

    internal void AddExternalFile(string fullPath)
    {
        string file = Path.GetFullPath(fullPath);
        Scopes.Add(new ProtectionScope("external-file", Path.GetDirectoryName(file)!,
            "external_file", Path.GetFileName(file)));
    }

    internal void AddExternalDirectory(string fullPath)
    {
        Scopes.Add(new ProtectionScope("external-dir", Path.GetFullPath(fullPath), "external_directory"));
    }

    public void Dispose()
    {
        if (!ownsDirectory) return;
        string parent = Path.GetDirectoryName(DirectoryPath)!;
        if (!Path.GetFileName(DirectoryPath).StartsWith("BlastRadiusFixture-", StringComparison.Ordinal) ||
            parent != Path.GetPathRoot(DirectoryPath))
            throw new InvalidOperationException("Refusing to remove a path outside a generated fixture.");
        try { if (Directory.Exists(DirectoryPath)) RemoveGeneratedTree(DirectoryPath); }
        catch
        {
            Console.Error.WriteLine("FIXTURE_CLEANUP_FAILED path=" + DirectoryPath);
            throw;
        }
    }

    private static void RemoveGeneratedTree(string directory)
    {
        foreach (string entry in Directory.GetFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(entry);
                else RemoveGeneratedTree(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }
        Directory.Delete(directory);
    }
}
