using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Blast;

internal sealed class PathGuard : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    private readonly List<string> paths = [];
    private readonly List<FileIdentity?> plannedIdentities = [];
    private readonly string canonicalRoot;
    private readonly string? scopeId;
    private readonly IReadOnlyDictionary<string, FileIdentity>? expectedDirectories;
    internal FileIdentity ParentIdentity { get; private set; }

    internal PathGuard(string root, string fullFilePath, string? scopeId = null,
        IReadOnlyDictionary<string, FileIdentity>? expectedDirectories = null)
    {
        if ((scopeId is null) != (expectedDirectories is null))
            throw new ArgumentException("Scope and fixed directory identities must be provided together.");
        this.scopeId = scopeId;
        this.expectedDirectories = expectedDirectories;
        canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string canonicalFile = Path.GetFullPath(fullFilePath);
        string relative = Path.GetRelativePath(canonicalRoot, canonicalFile);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative == ".") throw new InvalidOperationException("Path escapes the protected root.");

        string parent = Path.GetDirectoryName(canonicalFile)!;
        string walk = canonicalRoot;
        try
        {
            Add(walk);
            string pathFromRoot = Path.GetRelativePath(canonicalRoot, parent);
            if (pathFromRoot != ".")
                foreach (string part in pathFromRoot.Split(Path.DirectorySeparatorChar))
                {
                    walk = Path.Combine(walk, part);
                    Add(walk);
                }
            ParentIdentity = WindowsFiles.Identity(handles[^1]);
            Check();
        }
        catch { Dispose(); throw; }
    }

    private void Add(string path)
    {
        var handle = WindowsFiles.OpenDirectory(path);
        var identity = WindowsFiles.Identity(handle);
        if (identity.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            handle.Dispose();
            throw new NotSupportedException("A protected parent is a reparse point.");
        }
        FileIdentity? planned = null;
        if (expectedDirectories is not null)
        {
            string key = scopeId + "|" + Path.GetRelativePath(canonicalRoot, path);
            if (!expectedDirectories.TryGetValue(key, out var expected))
            {
                handle.Dispose();
                throw new InvalidOperationException("Protected directory has no fixed-plan identity: " + key);
            }
            if (identity != expected)
            {
                handle.Dispose();
                throw new InvalidOperationException("Protected directory identity differs from fixed plan: " + key);
            }
            planned = expected;
        }
        handles.Add(handle);
        paths.Add(path);
        plannedIdentities.Add(planned);
    }

    internal void Check()
    {
        for (int i = 0; i < handles.Count; i++)
        {
            string actual = WindowsFiles.FinalPath(handles[i]);
            string expected = @"\\?\" + Path.GetFullPath(paths[i]);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A protected parent path moved while in use.");
            if (plannedIdentities[i] is FileIdentity planned && WindowsFiles.Identity(handles[i]) != planned)
                throw new IOException("Protected directory identity changed while in use: " +
                    scopeId + "|" + Path.GetRelativePath(canonicalRoot, paths[i]));
        }
    }

    public void Dispose()
    {
        foreach (var handle in handles) handle.Dispose();
        handles.Clear();
    }
}

internal sealed record CapturedFile(FileState State, byte[]? Bytes);

internal static class FileSystemScope
{
    internal const int MaxFileBytes = 8 * 1024 * 1024;
    internal static Action<string>? AfterPathAttributesForTest { get; set; }

    internal static string FullPath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rel = Path.GetRelativePath(Path.GetFullPath(root), full);
        if (Path.IsPathRooted(rel) || rel == ".." ||
            rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || rel == ".")
            throw new InvalidOperationException("Path escapes the protected root.");
        return full;
    }

    internal static CapturedFile Capture(string root, string relative, ObjectStore? objects = null)
    {
        string full = FullPath(root, relative);
        try
        {
            using var parents = new PathGuard(root, full);
            return CaptureWithGuard(relative, full, parents, objects);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return new(FileState.Unknown(relative, ex.GetType().Name + ": " + ex.Message,
                ex is NotSupportedException ? CoverageFailure.UnsupportedType : CoverageFailure.ReadFailure), null);
        }
    }

    // The executor holds this guard across the mutation and verification. Opening
    // a second PathGuard would require releasing the first and create a race.
    internal static CapturedFile CaptureWithGuard(string relative, string full, PathGuard parents,
        ObjectStore? objects = null)
    {
        try
        {
            parents.Check();
            FileAttributes attributes;
            try { attributes = File.GetAttributes(full); }
            catch (FileNotFoundException) { return new(FileState.Absent(relative, parents.ParentIdentity), null); }
            catch (DirectoryNotFoundException) { return new(FileState.Unknown(relative, "Parent directory missing."), null); }
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.SparseFile) ||
                attributes.HasFlag(FileAttributes.Offline))
                return new(FileState.Unknown(relative, "Unsupported file type or attribute.", CoverageFailure.UnsupportedType), null);

            AfterPathAttributesForTest?.Invoke(full);
            using var handle = WindowsFiles.OpenFile(full, write: false);
            FileIdentity identity = WindowsFiles.ValidateSupportedLeaf(handle, full);
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length > MaxFileBytes)
                return new(FileState.Unknown(relative, "File exceeds the current size limit.", CoverageFailure.UnsupportedType), null);
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.Length != bytes.Length || WindowsFiles.ValidateSupportedLeaf(handle, full) != identity)
                return new(FileState.Unknown(relative, "File identity or length changed during capture."), null);
            stream.Position = 0;
            byte[] secondRead = new byte[bytes.Length];
            stream.ReadExactly(secondRead);
            if (!bytes.AsSpan().SequenceEqual(secondRead) || stream.Length != bytes.Length)
                return new(FileState.Unknown(relative, "File content changed during capture."), null);
            parents.Check();
            string hash = ObjectStore.Hash(bytes);
            string? objectId = objects?.Save(bytes);
            return new(new FileState(Presence.Present, relative, hash, objectId, bytes.Length,
                identity.Volume, identity.Index, identity.Links, identity.Attributes,
                parents.ParentIdentity.Volume, parents.ParentIdentity.Index), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return new(FileState.Unknown(relative, ex.GetType().Name + ": " + ex.Message,
                ex is NotSupportedException ? CoverageFailure.UnsupportedType : CoverageFailure.ReadFailure), null);
        }
    }

    internal static Dictionary<string, FileState> CaptureManifest(string root, ObjectStore objects,
        CoverageRecord? coverage = null, Dictionary<string, FileIdentity>? directories = null)
    {
        var result = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string directory = stack.Pop();
            if (directories is not null)
            {
                using var directoryHandle = WindowsFiles.OpenDirectory(directory);
                var directoryIdentity = WindowsFiles.Identity(directoryHandle);
                if (directoryIdentity.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new NotSupportedException("Protected directory is a reparse point.");
                directories.Add(Path.GetRelativePath(root, directory), directoryIdentity);
            }
            IEnumerable<string> entries;
            try { entries = Directory.GetFileSystemEntries(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Incomplete directory enumeration: " + directory, ex);
            }
            foreach (string path in entries)
            {
                string relative = Path.GetRelativePath(root, path);
                if (relative.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                {
                    coverage?.ConfirmedExclusions.Add(relative);
                    continue;
                }
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result[relative] = FileState.Unknown(relative, ex.Message);
                    coverage?.ReadFailures.Add(relative);
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    result[relative] = FileState.Unknown(relative, "Reparse point is unsupported.", CoverageFailure.UnsupportedType);
                    coverage?.UnsupportedTypes.Add(relative);
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.Directory)) stack.Push(path);
                else
                {
                    var captured = Capture(root, relative, objects).State;
                    result[relative] = captured;
                    if (captured.Presence == Presence.Present) coverage?.CompleteBaselines.Add(relative);
                    else if (captured.FailureKind == CoverageFailure.UnsupportedType)
                        coverage?.UnsupportedTypes.Add(relative);
                    else coverage?.ReadFailures.Add(relative);
                }
            }
        }
        if (directories is not null)
        {
            foreach (var (relative, expected) in directories)
            {
                string path = relative == "." ? root : Path.Combine(root, relative);
                using var handle = WindowsFiles.OpenDirectory(path);
                if (WindowsFiles.Identity(handle) != expected)
                    throw new IOException("Protected directory identity changed during scan: " + relative);
            }
        }
        return result;
    }
}
