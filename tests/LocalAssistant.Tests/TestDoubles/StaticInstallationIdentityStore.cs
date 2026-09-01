using LocalAssistant.Api.Security;

namespace LocalAssistant.Tests.TestDoubles;

internal sealed class StaticInstallationIdentityStore(InstallationIdentity identity) : IInstallationIdentityStore
{
    public ValueTask<InstallationIdentity?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<InstallationIdentity?>(identity);
    }

    public ValueTask<InstallationBootstrapResult> BootstrapAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new InstallationBootstrapResult(
            InstallationBootstrapStatus.Created,
            identity.OwnerPrincipalId));
    }
}
