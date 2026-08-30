using System.Text;

namespace LocalAssistant.Infrastructure.Documents;

internal static class DocumentFilePolicy
{
    public const long MaximumFileSizeBytes = 1024 * 1024;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv",
        ".json",
        ".md",
        ".txt",
    };

    public static string? ResolveSearchPath(string rootPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return rootPath;
        }

        var searchPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        if (!IsWithinRoot(rootPath, searchPath) || !Directory.Exists(searchPath))
        {
            return null;
        }

        try
        {
            return ContainsReparsePoint(rootPath, searchPath) ? null : searchPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsAuthorizedFile(string rootPath, string filePath)
    {
        return IsWithinRoot(rootPath, filePath) &&
            File.Exists(filePath) &&
            !ContainsReparsePoint(rootPath, filePath) &&
            (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) == 0;
    }

    public static bool IsSupportedTextFormat(FileInfo file)
    {
        return SupportedExtensions.Contains(file.Extension);
    }

    public static bool IsWithinMaximumSize(FileInfo file)
    {
        return file.Length <= MaximumFileSizeBytes;
    }

    public static async ValueTask<string?> ReadBoundedTextAsync(
        string rootPath,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorizedFile(rootPath, filePath))
        {
            return null;
        }

        await using var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var buffer = new MemoryStream();
        var readBuffer = new byte[81920];

        while (true)
        {
            var bytesRead = await fileStream.ReadAsync(readBuffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (buffer.Length + bytesRead > MaximumFileSizeBytes)
            {
                return null;
            }

            await buffer.WriteAsync(readBuffer.AsMemory(0, bytesRead), cancellationToken);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        var normalizedRootPath = Path.GetFullPath(rootPath);
        var normalizedCandidatePath = Path.GetFullPath(candidatePath);
        var relativePath = Path.GetRelativePath(normalizedRootPath, normalizedCandidatePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return StringComparer.FromComparison(comparison).Equals(
            Path.GetFullPath(Path.Combine(normalizedRootPath, relativePath)),
            normalizedCandidatePath);
    }

    private static bool ContainsReparsePoint(string rootPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        var currentPath = rootPath;

        foreach (var segment in relativePath.Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment == ".")
            {
                continue;
            }

            currentPath = Path.Combine(currentPath, segment);
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
