using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Blast;

internal readonly record struct FileIdentity(uint Volume, ulong Index, uint Links, FileAttributes Attributes);

internal static class WindowsFiles
{
    internal static Action? BeforeStreamQueryForTest { get; set; }
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint ReadAttributes = 0x00000080;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;

    internal static SafeFileHandle OpenFile(string path, bool write, bool delete = false) =>
        Open(path, GenericRead | (write ? GenericWrite : 0) | (delete ? Delete : 0), 0, OpenReparsePoint);

    internal static SafeFileHandle OpenDirectory(string path) =>
        Open(path, ReadAttributes | Delete, ShareRead | ShareWrite, BackupSemantics | OpenReparsePoint);

    internal static SafeFileHandle OpenDirectoryReadAttributesOnlyForTest(string path) =>
        Open(path, ReadAttributes, ShareRead | ShareWrite, BackupSemantics | OpenReparsePoint);

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open {path}: {new Win32Exception(error).Message}");
        }
        return handle;
    }

    internal static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            info.NumberOfLinks, (FileAttributes)info.FileAttributes);
    }

    internal static FileIdentity ValidateSupportedLeaf(SafeFileHandle handle, string expectedPath)
    {
        var identity = Identity(handle);
        if (identity.Attributes.HasFlag(FileAttributes.Directory) ||
            identity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            identity.Attributes.HasFlag(FileAttributes.SparseFile) ||
            identity.Attributes.HasFlag(FileAttributes.Offline) || identity.Links != 1)
            throw new NotSupportedException("Leaf is not a supported single-link ordinary file.");
        string actual = FinalPath(handle);
        string expected = @"\\?\" + Path.GetFullPath(expectedPath);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Opened leaf is not at the checked path.");
        EnsureDefaultStreamOnly(handle);
        return identity;
    }

    private static void EnsureDefaultStreamOnly(SafeFileHandle handle)
    {
        BeforeStreamQueryForTest?.Invoke();
        int size = 4096;
        while (size <= 1024 * 1024)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetFileInformationByHandleEx(handle, 7, buffer, (uint)size))
                {
                    int offset = 0;
                    int entries = 0;
                    while (true)
                    {
                        if (size - offset < 24 || ++entries > 4096)
                            throw new InvalidDataException("Invalid stream information buffer.");
                        int next = Marshal.ReadInt32(buffer, offset);
                        int nameBytes = Marshal.ReadInt32(buffer, offset + 4);
                        int available = next == 0 ? size - offset : next;
                        if (nameBytes < 0 || (nameBytes & 1) != 0 ||
                            available < 24 || nameBytes > available - 24)
                            throw new InvalidDataException("Invalid stream information entry.");
                        string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 24), nameBytes / 2) ?? "";
                        if (!name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase))
                            throw new NotSupportedException("Named data stream is unsupported: " + name);
                        if (next == 0) return;
                        if (next < 24 || offset > size - next)
                            throw new InvalidDataException("Invalid stream information offset.");
                        offset += next;
                    }
                }
                int error = Marshal.GetLastWin32Error();
                if (error is not (122 or 234))
                    throw new Win32Exception(error, "Cannot enumerate streams of opened file.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
            size *= 2;
        }
        throw new NotSupportedException("Stream information exceeds supported bound.");
    }

    internal static void DeleteByHandle(SafeFileHandle handle)
    {
        var info = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandleDisposition(handle, 4, ref info, (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void RenameByHandle(SafeFileHandle handle, string destination)
    {
        // FILE_RENAME_INFO on x64: BOOLEAN + padding + HANDLE + DWORD + UTF-16 file name.
        byte[] name = System.Text.Encoding.Unicode.GetBytes(destination);
        int offset = IntPtr.Size == 8 ? 20 : 12;
        IntPtr buffer = Marshal.AllocHGlobal(offset + name.Length + 2);
        try
        {
            for (int i = 0; i < offset; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, offset), name.Length);
            Marshal.WriteInt16(buffer, offset + name.Length, 0);
            if (!SetFileInformationByHandleRaw(handle, 3, buffer, (uint)(offset + name.Length + 2)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new System.Text.StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }

    internal static void CreateHardLinkForTest(string link, string existing)
    {
        if (!CreateHardLinkW(link, existing, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        IntPtr information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    private static extern bool SetFileInformationByHandleDisposition(SafeFileHandle file, int infoClass,
        ref FileDispositionInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    private static extern bool SetFileInformationByHandleRaw(SafeFileHandle file, int infoClass,
        IntPtr info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, System.Text.StringBuilder path,
        uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
