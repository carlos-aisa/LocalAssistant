using LocalAssistant.Core.Documents;

namespace LocalAssistant.Tests.Documents;

public sealed class DocumentSearchQueryTests
{
    [Fact]
    public void AcceptsAnExtensionWithAPeriod()
    {
        var query = new DocumentSearchQuery(extension: ".md");

        Assert.Equal(".md", query.Extension);
    }

    [Fact]
    public void RejectsAnExtensionWithoutAPeriod()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new DocumentSearchQuery(extension: "md"));

        Assert.Equal("extension", exception.ParamName);
    }

    [Fact]
    public void RejectsAnAbsoluteDocumentPath()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new DocumentSearchQuery(relativePath: Path.GetTempPath()));

        Assert.Equal("relativePath", exception.ParamName);
    }

    [Fact]
    public void RejectsAnInvalidModificationDateRange()
    {
        var after = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var before = after.AddDays(-1);

        Assert.Throws<ArgumentException>(() =>
            new DocumentSearchQuery(modifiedAfterUtc: after, modifiedBeforeUtc: before));
    }
}
