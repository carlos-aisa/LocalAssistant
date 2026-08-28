using LocalAssistant.Infrastructure.Documents;
using Microsoft.AspNetCore.DataProtection;

namespace LocalAssistant.Tests.Infrastructure;

public sealed class ProtectedDocumentReferenceProtectorTests
{
    [Fact]
    public void ProtectsAndRestoresRelativePathsBeforeExpiration()
    {
        using var directory = new TemporaryDirectory();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));
        var protector = CreateProtector(directory.Path, timeProvider);

        var documentReference = protector.Protect(Path.Combine("projects", "notes.md"));

        Assert.True(protector.TryUnprotect(documentReference, out var relativePath));
        Assert.Equal(Path.Combine("projects", "notes.md"), relativePath);
        Assert.DoesNotContain("notes.md", documentReference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsExpiredReferences()
    {
        using var directory = new TemporaryDirectory();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));
        var protector = CreateProtector(directory.Path, timeProvider);
        var documentReference = protector.Protect("notes.md");
        timeProvider.Advance(TimeSpan.FromMinutes(15));

        var isValid = protector.TryUnprotect(documentReference, out _);

        Assert.False(isValid);
    }

    [Fact]
    public void RejectsModifiedReferences()
    {
        using var directory = new TemporaryDirectory();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));
        var protector = CreateProtector(directory.Path, timeProvider);
        var documentReference = protector.Protect("notes.md");

        var isValid = protector.TryUnprotect(documentReference + "x", out _);

        Assert.False(isValid);
    }

    private static ProtectedDocumentReferenceProtector CreateProtector(
        string keyDirectory,
        TimeProvider timeProvider)
    {
        var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(keyDirectory));
        return new ProtectedDocumentReferenceProtector(dataProtectionProvider, timeProvider);
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan elapsed)
        {
            _utcNow = _utcNow.Add(elapsed);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalAssistant.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
