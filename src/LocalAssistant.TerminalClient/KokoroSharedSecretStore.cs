using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace LocalAssistant.TerminalClient;

internal interface IKokoroSharedSecretProtector
{
    bool IsAvailable { get; }

    byte[] Protect(byte[] clearBytes);

    byte[] Unprotect(byte[] protectedBytes);
}

internal interface IKokoroSharedSecretFileSystem
{
    bool FileExists(string path);

    byte[] ReadAllBytes(string path);

    void WriteAllBytes(string path, byte[] contents);

    bool EnsurePrivateDirectory(string path);

    void ReplaceAtomically(string sourcePath, string destinationPath);

    void DeleteFile(string path);
}

internal interface IKokoroSharedSecretProcessLock : IDisposable
{
    bool TryAcquire();
}

/// <summary>
/// Stores the secret shared only by the loopback Kokoro process and this Windows user.
/// The envelope is deliberately binary and consumed independently by .NET and Python.
/// </summary>
public sealed class KokoroSharedSecretStore
{
    private static ReadOnlySpan<byte> Magic => "LASKOK01"u8;
    private const ushort Version = 1;
    private const byte CurrentUserScope = 1;
    private const int HeaderLength = 15;
    private const int SecretLength = 32;
    private const int MaximumProtectedBlobLength = 1024 * 1024;

    private readonly string _path;
    private readonly IKokoroSharedSecretProtector _protector;
    private readonly IKokoroSharedSecretFileSystem _fileSystem;
    private readonly Func<IKokoroSharedSecretProcessLock> _lockFactory;

    public KokoroSharedSecretStore()
        : this(DefaultPath)
    {
    }

    internal KokoroSharedSecretStore(
        string path,
        IKokoroSharedSecretProtector protector,
        IKokoroSharedSecretFileSystem fileSystem,
        Func<IKokoroSharedSecretProcessLock> lockFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _lockFactory = lockFactory ?? throw new ArgumentNullException(nameof(lockFactory));
    }

    public KokoroSharedSecretStore(string path)
        : this(
            path,
            CreateDefaultProtector(),
            new SystemKokoroSharedSecretFileSystem(),
            static () => new WindowsKokoroSharedSecretProcessLock())
    {
    }

    internal static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAssistant",
        "Kokoro",
        "shared-secret.v1.dpapi");

    internal string Path => _path;

    internal bool Exists => _fileSystem.FileExists(_path);

    internal byte[]? Read()
    {
        if (!_protector.IsAvailable || !_fileSystem.FileExists(_path))
        {
            return null;
        }

        try
        {
            return Decode(_fileSystem.ReadAllBytes(_path), _protector);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
            UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    internal bool Provision()
    {
        if (!_protector.IsAvailable || _fileSystem.FileExists(_path))
        {
            return false;
        }

        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        try
        {
            return WriteNewSecret(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    internal bool Rotate()
    {
        using var processLock = _lockFactory();
        if (!processLock.TryAcquire())
        {
            return false;
        }

        var previous = Read();
        if (previous is null)
        {
            return false;
        }

        var replacement = RandomNumberGenerator.GetBytes(SecretLength);
        try
        {
            if (WriteNewSecret(replacement))
            {
                return true;
            }

            RestorePreviousSecret(previous);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(previous);
            CryptographicOperations.ZeroMemory(replacement);
        }
    }

    internal static byte[] Decode(ReadOnlySpan<byte> envelope, IKokoroSharedSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (envelope.Length < HeaderLength || !envelope[..Magic.Length].SequenceEqual(Magic))
        {
            throw new FormatException("The Kokoro shared-secret envelope is invalid.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(envelope.Slice(8, sizeof(ushort)));
        var scope = envelope[10];
        var protectedLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(11, sizeof(int)));
        if (version != Version || scope != CurrentUserScope || protectedLength <= 0 ||
            protectedLength > MaximumProtectedBlobLength || envelope.Length != HeaderLength + protectedLength)
        {
            throw new FormatException("The Kokoro shared-secret envelope is invalid.");
        }

        var protectedBytes = envelope[HeaderLength..].ToArray();
        byte[]? secret = null;
        try
        {
            secret = protector.Unprotect(protectedBytes);
            if (secret.Length != SecretLength)
            {
                throw new CryptographicException("The Kokoro shared secret has an invalid length.");
            }

            return secret;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    internal static byte[] Encode(ReadOnlySpan<byte> secret, IKokoroSharedSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (secret.Length != SecretLength)
        {
            throw new ArgumentException("The Kokoro shared secret must have exactly 32 bytes.", nameof(secret));
        }

        var clearBytes = secret.ToArray();
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = protector.Protect(clearBytes);
            if (protectedBytes.Length == 0 || protectedBytes.Length > MaximumProtectedBlobLength)
            {
                throw new CryptographicException("The protected Kokoro shared secret is invalid.");
            }

            var envelope = new byte[HeaderLength + protectedBytes.Length];
            Magic.CopyTo(envelope);
            BinaryPrimitives.WriteUInt16LittleEndian(envelope.AsSpan(8, sizeof(ushort)), Version);
            envelope[10] = CurrentUserScope;
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(11, sizeof(int)), protectedBytes.Length);
            protectedBytes.CopyTo(envelope.AsSpan(HeaderLength));
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    private bool WriteNewSecret(ReadOnlySpan<byte> secret)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory) || !_fileSystem.EnsurePrivateDirectory(directory))
        {
            return false;
        }

        byte[]? envelope = null;
        string? temporaryPath = null;
        try
        {
            envelope = Encode(secret, _protector);
            temporaryPath = System.IO.Path.Combine(
                directory,
                $"{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            _fileSystem.WriteAllBytes(temporaryPath, envelope);
            _fileSystem.ReplaceAtomically(temporaryPath, _path);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (envelope is not null)
            {
                CryptographicOperations.ZeroMemory(envelope);
            }

            if (temporaryPath is not null && _fileSystem.FileExists(temporaryPath))
            {
                try
                {
                    _fileSystem.DeleteFile(temporaryPath);
                }
                catch (IOException)
                {
                    // An unsuccessful cleanup must not alter the result of the operation.
                }
                catch (UnauthorizedAccessException)
                {
                    // An unsuccessful cleanup must not alter the result of the operation.
                }
            }
        }
    }

    private bool RestorePreviousSecret(ReadOnlySpan<byte> previous) => WriteNewSecret(previous);

    private static IKokoroSharedSecretProtector CreateDefaultProtector() =>
        OperatingSystem.IsWindows()
            ? CreateWindowsProtector()
            : UnavailableKokoroSharedSecretProtector.Instance;

    [SupportedOSPlatform("windows")]
    private static WindowsKokoroSharedSecretProtector CreateWindowsProtector() => new();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsKokoroSharedSecretProtector : IKokoroSharedSecretProtector
{
    private const int CryptprotectUiForbidden = 0x1;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] clearBytes) => ProtectOrUnprotect(clearBytes, protect: true);

    public byte[] Unprotect(byte[] protectedBytes) => ProtectOrUnprotect(protectedBytes, protect: false);

    private static byte[] ProtectOrUnprotect(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob { ByteCount = input.Length, Data = inputPointer };
            var success = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out outputBlob);
            if (!success)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var output = new byte[outputBlob.ByteCount];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                {
                    LocalFree(outputBlob.Data);
                }
            }
        }
        finally
        {
            if (input.Length > 0)
            {
                CryptographicOperations.ZeroMemory(input);
            }

            Marshal.FreeHGlobal(inputPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int ByteCount;

        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class SystemKokoroSharedSecretFileSystem : IKokoroSharedSecretFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] contents) => File.WriteAllBytes(path, contents);

    public bool EnsurePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return EnsureWindowsPrivateDirectory(path);
    }

    public void ReplaceAtomically(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(sourcePath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            return;
        }

        File.Move(sourcePath, destinationPath);
    }

    public void DeleteFile(string path) => File.Delete(path);

    [SupportedOSPlatform("windows")]
    private static bool EnsureWindowsPrivateDirectory(string path)
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                return false;
            }

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));

            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                directory.Create();
            }

            directory.SetAccessControl(security);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            PlatformNotSupportedException or IdentityNotMappedException)
        {
            return false;
        }
    }
}

internal sealed class WindowsKokoroSharedSecretProcessLock : IKokoroSharedSecretProcessLock
{
    private const string Name = "Local\\LocalAssistant.Kokoro.SharedSecret.v1";
    private Mutex? _mutex;
    private bool _ownsMutex;

    public bool TryAcquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        _mutex = new Mutex(initiallyOwned: false, Name);
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
            return _ownsMutex;
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
            return true;
        }
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }
}

internal sealed class UnavailableKokoroSharedSecretProtector : IKokoroSharedSecretProtector
{
    public static UnavailableKokoroSharedSecretProtector Instance { get; } = new();

    public bool IsAvailable => false;

    public byte[] Protect(byte[] clearBytes) => throw new PlatformNotSupportedException();

    public byte[] Unprotect(byte[] protectedBytes) => throw new PlatformNotSupportedException();
}
