using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CloudScribe.Application.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Security;

public sealed class WindowsCredentialVault : ICredentialVault
{
    private const uint GenericCredentialType = 1;
    private const uint PersistLocalMachine = 2;
    private const int MaximumCredentialBlobBytes = 5 * 512;
    private const int ErrorNotFound = 1168;
    private const string TargetPrefix = "CloudScribePro/";

    public ValueTask StoreAsync(
        CredentialReference reference,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.IsEmpty)
        {
            throw new ArgumentException("Credential secret cannot be empty.", nameof(secret));
        }

        byte[] credentialBytes = new byte[Encoding.Unicode.GetByteCount(secret.Span)];
        _ = Encoding.Unicode.GetBytes(secret.Span, credentialBytes);
        if (credentialBytes.Length > MaximumCredentialBlobBytes)
        {
            Array.Clear(credentialBytes);
            throw new ArgumentOutOfRangeException(nameof(secret), "Credential secret exceeds the Windows Credential Manager blob limit.");
        }

        IntPtr targetPointer = IntPtr.Zero;
        GCHandle pinnedSecret = default;
        try
        {
            targetPointer = Marshal.StringToHGlobalUni(BuildTarget(reference));
            pinnedSecret = GCHandle.Alloc(credentialBytes, GCHandleType.Pinned);
            NativeCredential credential = new()
            {
                Type = GenericCredentialType,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)credentialBytes.Length),
                CredentialBlob = pinnedSecret.AddrOfPinnedObject(),
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw CreateWin32Exception("CredWriteW");
            }
        }
        finally
        {
            Array.Clear(credentialBytes);
            if (pinnedSecret.IsAllocated)
            {
                pinnedSecret.Free();
            }
            if (targetPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(targetPointer);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<CredentialSecret?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(BuildTarget(reference), GenericCredentialType, 0, out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return ValueTask.FromResult<CredentialSecret?>(null);
            }
            throw new Win32Exception(error, "CredReadW failed while reading the CloudScribe credential reference.");
        }

        byte[]? credentialBytes = null;
        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return ValueTask.FromResult<CredentialSecret?>(null);
            }
            if (credential.CredentialBlobSize > MaximumCredentialBlobBytes || credential.CredentialBlobSize % 2 != 0)
            {
                throw new InvalidDataException("Windows Credential Manager returned an invalid CloudScribe credential blob.");
            }
            credentialBytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, credentialBytes, 0, credentialBytes.Length);
            char[] secret = Encoding.Unicode.GetChars(credentialBytes);
            return ValueTask.FromResult<CredentialSecret?>(new CredentialSecret(secret));
        }
        finally
        {
            if (credentialBytes is not null)
            {
                Array.Clear(credentialBytes);
            }
            CredFree(credentialPointer);
        }
    }

    public ValueTask<bool> DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (CredDelete(BuildTarget(reference), GenericCredentialType, 0))
        {
            return ValueTask.FromResult(true);
        }
        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound)
        {
            return ValueTask.FromResult(false);
        }
        throw new Win32Exception(error, "CredDeleteW failed while deleting the CloudScribe credential reference.");
    }

    private static string BuildTarget(CredentialReference reference) => TargetPrefix + reference.TargetName;

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CloudScribe provider credentials use Windows Credential Manager on Windows.");
        }
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation} failed for the CloudScribe credential vault.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}
