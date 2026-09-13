using System.Diagnostics;
using System.Security.Cryptography;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class KokoroDpapiInteropTests
{
    [Fact]
    public async Task TemporaryEnvelopeCanBeReadByBothRuntimes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pythonPath = Environment.GetEnvironmentVariable("LOCALASSISTANT_KOKORO_PYTHON_PATH");
        var serviceRoot = Environment.GetEnvironmentVariable("LOCALASSISTANT_KOKORO_SERVICE_ROOT");
        var isRequired = string.Equals(
            Environment.GetEnvironmentVariable("LOCALASSISTANT_KOKORO_REQUIRE_INTEROP"),
            "1",
            StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(pythonPath) || string.IsNullOrWhiteSpace(serviceRoot))
        {
            Assert.False(isRequired, "The opt-in Kokoro DPAPI interoperability test is missing its runtime paths.");
            return;
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LocalAssistant.Kokoro.Interop.{Guid.NewGuid():N}");
        var temporarySecretPath = Path.Combine(temporaryDirectory, "shared-secret.v1.dpapi");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var store = new KokoroSharedSecretStore(temporarySecretPath);
            Assert.True(store.Provision());

            await RunPythonInteropAsync(pythonPath, serviceRoot, "--read", temporarySecretPath);

            File.Delete(temporarySecretPath);
            await RunPythonInteropAsync(pythonPath, serviceRoot, "--write", temporarySecretPath);

            var secret = store.Read();
            try
            {
                Assert.NotNull(secret);
                Assert.Equal(32, secret.Length);
            }
            finally
            {
                if (secret is not null)
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static async Task RunPythonInteropAsync(
        string pythonPath,
        string serviceRoot,
        string operation,
        string temporarySecretPath)
    {
        var startInfo = new ProcessStartInfo(pythonPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("localassistant_kokoro_tts.interop");
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(temporarySecretPath);
        startInfo.Environment["PYTHONPATH"] = Path.Combine(serviceRoot, "src");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(process.ExitCode == 0, "The Python DPAPI interoperability helper failed.");
    }
}
