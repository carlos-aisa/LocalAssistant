using System.Security.Cryptography;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class KokoroSharedSecretStoreTests
{
    [Fact]
    public void EncodeAndDecodeRoundTripUsesTheStrictEnvelope()
    {
        var protector = new ReversibleProtector();
        var secret = RandomNumberGenerator.GetBytes(32);

        var envelope = KokoroSharedSecretStore.Encode(secret, protector);
        var decoded = KokoroSharedSecretStore.Decode(envelope, protector);

        Assert.Equal("LASKOK01"u8.ToArray(), envelope[..8]);
        Assert.Equal(secret, decoded);
    }

    [Theory]
    [MemberData(nameof(CorruptEnvelopes))]
    public void DecodeRejectsCorruptEnvelopeBeforeItCanBeUsed(byte[] envelope)
    {
        var protector = new ReversibleProtector();

        Assert.ThrowsAny<Exception>(() => KokoroSharedSecretStore.Decode(envelope, protector));
    }

    [Fact]
    public void ProvisionWritesSecretToTheInjectedTemporaryStore()
    {
        var fileSystem = new InMemoryFileSystem();
        var store = CreateStore(fileSystem, new ReversibleProtector());

        var provisioned = store.Provision();
        var secret = store.Read();

        Assert.True(provisioned);
        Assert.NotNull(secret);
        Assert.Equal(32, secret.Length);
        Assert.DoesNotContain(KokoroSharedSecretStore.DefaultPath, fileSystem.Paths);
    }

    [Fact]
    public void RotateRefusesToModifySecretWhileProcessLockIsHeld()
    {
        var fileSystem = new InMemoryFileSystem();
        var store = CreateStore(fileSystem, new ReversibleProtector(), lockAcquired: false);
        Assert.True(store.Provision());
        var previous = store.Read();

        var rotated = store.Rotate();

        Assert.False(rotated);
        Assert.Equal(previous, store.Read());
    }

    [Fact]
    public void FailedRotationRollsBackToPreviousSecretInTheTemporaryStore()
    {
        var fileSystem = new InMemoryFileSystem { FailNextReplacement = false };
        var store = CreateStore(fileSystem, new ReversibleProtector());
        Assert.True(store.Provision());
        var previous = store.Read();
        fileSystem.FailNextReplacement = true;

        var rotated = store.Rotate();

        Assert.False(rotated);
        Assert.Equal(previous, store.Read());
    }

    [Fact]
    public void CorruptionReturnsNullWithoutOverwritingTheTemporaryStore()
    {
        var fileSystem = new InMemoryFileSystem();
        var store = CreateStore(fileSystem, new ReversibleProtector());
        Assert.True(store.Provision());
        fileSystem.ReplaceContent("C:\\temporary\\shared-secret.v1.dpapi", [1, 2, 3]);

        Assert.Null(store.Read());
        Assert.Equal(new byte[] { 1, 2, 3 }, fileSystem.ReadAllBytes("C:\\temporary\\shared-secret.v1.dpapi"));
    }

    public static IEnumerable<object[]> CorruptEnvelopes()
    {
        yield return [Array.Empty<byte>()];
        yield return ["LASKOK00"u8.ToArray()];
        yield return [BuildEnvelope(version: 1, scope: 1, protectedLength: 0, payload: [])];
        yield return [BuildEnvelope(version: 2, scope: 1, protectedLength: 1, payload: [0])];
        yield return [BuildEnvelope(version: 1, scope: 2, protectedLength: 1, payload: [0])];
    }

    private static KokoroSharedSecretStore CreateStore(
        InMemoryFileSystem fileSystem,
        IKokoroSharedSecretProtector protector,
        bool lockAcquired = true) => new(
            "C:\\temporary\\shared-secret.v1.dpapi",
            protector,
            fileSystem,
            () => new TestProcessLock(lockAcquired));

    private static byte[] BuildEnvelope(ushort version, byte scope, int protectedLength, byte[] payload)
    {
        var envelope = new byte[15 + payload.Length];
        "LASKOK01"u8.CopyTo(envelope);
        BitConverter.GetBytes(version).CopyTo(envelope, 8);
        envelope[10] = scope;
        BitConverter.GetBytes(protectedLength).CopyTo(envelope, 11);
        payload.CopyTo(envelope, 15);
        return envelope;
    }

    private sealed class ReversibleProtector : IKokoroSharedSecretProtector
    {
        public bool IsAvailable => true;

        public byte[] Protect(byte[] clearBytes) => clearBytes.Reverse().ToArray();

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.Reverse().ToArray();
    }

    private sealed class TestProcessLock(bool acquired) : IKokoroSharedSecretProcessLock
    {
        public bool TryAcquire() => acquired;

        public void Dispose()
        {
        }
    }

    private sealed class InMemoryFileSystem : IKokoroSharedSecretFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public bool FailNextReplacement { get; set; }

        public IReadOnlyCollection<string> Paths => _files.Keys;

        public bool FileExists(string path) => _files.ContainsKey(path);

        public byte[] ReadAllBytes(string path) => _files[path].ToArray();

        public void WriteAllBytes(string path, byte[] contents) => _files[path] = contents.ToArray();

        public bool EnsurePrivateDirectory(string path) => path == "C:\\temporary";

        public void ReplaceAtomically(string sourcePath, string destinationPath)
        {
            if (FailNextReplacement)
            {
                FailNextReplacement = false;
                throw new IOException("Synthetic replacement failure.");
            }

            _files[destinationPath] = _files[sourcePath];
            _files.Remove(sourcePath);
        }

        public void DeleteFile(string path) => _files.Remove(path);

        public void ReplaceContent(string path, byte[] contents) => _files[path] = contents.ToArray();
    }
}
