using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAssistant.Core.Documents;
using Microsoft.AspNetCore.DataProtection;

namespace LocalAssistant.Infrastructure.Documents;

public sealed class ProtectedDocumentReferenceProtector : IDocumentReferenceProtector
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    public ProtectedDocumentReferenceProtector(
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(
            "LocalAssistant.Documents.Reference.v1");
        _timeProvider = timeProvider;
    }

    public string Protect(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var payload = new DocumentReferencePayload(
            relativePath,
            _timeProvider.GetUtcNow().Add(Lifetime));
        var serializedPayload = JsonSerializer.Serialize(payload);
        var protectedPayload = _protector.Protect(Encoding.UTF8.GetBytes(serializedPayload));
        return Base64UrlTextEncoder.Encode(protectedPayload);
    }

    public bool TryUnprotect(string documentReference, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(documentReference))
        {
            return false;
        }

        try
        {
            var protectedPayload = Base64UrlTextEncoder.Decode(documentReference);
            var serializedPayload = Encoding.UTF8.GetString(_protector.Unprotect(protectedPayload));
            var payload = JsonSerializer.Deserialize<DocumentReferencePayload>(serializedPayload);
            if (payload is null ||
                payload.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
                string.IsNullOrWhiteSpace(payload.RelativePath) ||
                Path.IsPathRooted(payload.RelativePath) ||
                Path.IsPathFullyQualified(payload.RelativePath))
            {
                return false;
            }

            relativePath = payload.RelativePath;
            return true;
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record DocumentReferencePayload(
        string RelativePath,
        DateTimeOffset ExpiresAtUtc);

    private static class Base64UrlTextEncoder
    {
        public static string Encode(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        public static byte[] Decode(string value)
        {
            var base64 = value
                .Replace('-', '+')
                .Replace('_', '/');
            var paddingLength = (4 - base64.Length % 4) % 4;
            return Convert.FromBase64String(base64.PadRight(base64.Length + paddingLength, '='));
        }
    }
}
