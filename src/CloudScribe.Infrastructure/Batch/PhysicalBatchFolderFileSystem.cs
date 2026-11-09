using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudScribe.Infrastructure.Batch;

internal sealed class PhysicalBatchFolderFileSystem : IBatchFolderFileSystem
{
    public static PhysicalBatchFolderFileSystem Instance { get; } = new();

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public string[] GetEntries(string folder) => Directory.GetFileSystemEntries(folder);

    public uint GetDirectoryReparseTag(string folder)
    {
        // Other platforms retain their fail-closed reparse/symlink policy and never load a Windows DLL.
        if (!OperatingSystem.IsWindows()) return 0;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        const int fileAttributeTagInfo = 9;
        // Zero desired access reads metadata only. Open the reparse point itself, not its target, and do not
        // hydrate file contents. Backup semantics permits opening a directory; no backup privilege is enabled.
        using var handle = CreateFile(Path.TrimEndingDirectorySeparator(folder), 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw MetadataError();
        if (!GetFileInformationByHandleEx(handle, fileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw MetadataError();
        const FileAttributes expected = FileAttributes.Directory | FileAttributes.ReparsePoint;
        if ((info.Attributes & expected) != expected)
            throw new IOException("The input folder changed during reparse metadata inspection.");
        return info.Tag;
    }

    private static IOException MetadataError() =>
        new("The input folder reparse metadata could not be read.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public FileAttributes Attributes;
        public uint Tag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out AttributeTagInfo information, uint bufferSize);
}
