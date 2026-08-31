using LocalAssistant.Api.Endpoints;
using LocalAssistant.Api.Fakes;
using LocalAssistant.Api.HostedServices;
using LocalAssistant.Api.LanguageModels;
using LocalAssistant.Api.Profiles;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Documents;
using LocalAssistant.Core.Memory;
using LocalAssistant.Core.Orchestration;
using LocalAssistant.Core.Profiles;
using LocalAssistant.Core.Reminders;
using LocalAssistant.Core.Security.PrivateClients;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.Conversations;
using LocalAssistant.Infrastructure.Documents;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using LocalAssistant.Infrastructure.Memory;
using LocalAssistant.Infrastructure.Security.PrivateClients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

var bootstrapOwnerRequested = args.Any(argument =>
    StringComparer.Ordinal.Equals(argument, "--bootstrap-owner"));
var bootstrapPrivateClientRequested = args.Any(argument =>
    StringComparer.Ordinal.Equals(argument, "--bootstrap-private-client"));
var createAdministrativeChallengeRequested = args.Any(argument =>
    StringComparer.Ordinal.Equals(argument, "--create-administrative-challenge"));
if (bootstrapOwnerRequested && args.Length != 1)
{
    throw new InvalidOperationException("The --bootstrap-owner command does not accept additional arguments.");
}

if (new[] { bootstrapOwnerRequested, bootstrapPrivateClientRequested, createAdministrativeChallengeRequested }
    .Count(requested => requested) > 1)
{
    throw new InvalidOperationException("Only one local administration command can be specified.");
}

var builder = WebApplication.CreateBuilder(
    bootstrapOwnerRequested || bootstrapPrivateClientRequested || createAdministrativeChallengeRequested ? [] : args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDataProtection();
builder.Services.AddSingleton<ISystemDocumentsPathProvider, SystemDocumentsPathProvider>();
builder.Services.AddSingleton<ILocalDocumentRoot, ConfiguredLocalDocumentRoot>();
builder.Services.AddSingleton<IDocumentReferenceProtector, ProtectedDocumentReferenceProtector>();
builder.Services.AddSingleton<ILocalDocumentSearch, FileSystemDocumentSearch>();
builder.Services.AddSingleton<FileSystemDocumentContentSearch>();
builder.Services.AddSingleton<IDocumentSemanticIndex>(services =>
{
    var options = services.GetRequiredService<IOptions<SqliteConversationStoreOptions>>().Value;
    var databasePath = options.DatabasePath
        ?? throw new InvalidOperationException("Document semantic indexing requires a persistence database path.");
    var directory = Path.GetDirectoryName(databasePath)
        ?? throw new InvalidOperationException("The persistence database directory is invalid.");
    return new SqliteDocumentSemanticIndex(Path.Combine(directory, "documents.db"));
});
builder.Services.AddSingleton<HybridDocumentContentSearch>(services => new HybridDocumentContentSearch(
    services.GetRequiredService<FileSystemDocumentContentSearch>(),
    services.GetRequiredService<ILocalDocumentRoot>(),
    services.GetRequiredService<IDocumentReferenceProtector>(),
    services.GetRequiredService<IDocumentSemanticIndex>(),
    services.GetRequiredService<ITextEmbeddingProvider>(),
    services.GetRequiredService<IOptions<DocumentSemanticSearchOptions>>().Value));
builder.Services.AddSingleton<ILocalDocumentContentSearch>(services =>
{
    var persistenceOptions = services.GetRequiredService<IOptions<SqliteConversationStoreOptions>>().Value;
    var ollamaOptions = services.GetRequiredService<IOptions<OllamaOptions>>().Value;
    return persistenceOptions.Enabled &&
        !string.IsNullOrWhiteSpace(persistenceOptions.DatabasePath) &&
        ollamaOptions.IsEmbeddingConfigured &&
        ollamaOptions.Endpoint.IsLoopback
        ? services.GetRequiredService<HybridDocumentContentSearch>()
        : services.GetRequiredService<FileSystemDocumentContentSearch>();
});
builder.Services.AddSingleton<ILocalDocumentContentReader, FileSystemDocumentContentReader>();
builder.Services.AddSingleton<InMemoryConversationStore>();
builder.Services.AddSingleton<SqliteConversationStore>();
builder.Services.AddSingleton<ConversationIndexingCoordinator>();
builder.Services.AddSingleton<NullConversationContextRetriever>();
builder.Services.AddSingleton<HybridConversationContextRetriever>(services =>
    new HybridConversationContextRetriever(
        services.GetRequiredService<SqliteConversationStore>(),
        services.GetRequiredService<ITextEmbeddingProvider>(),
        services.GetRequiredService<IOptions<ConversationRetrievalOptions>>().Value));
builder.Services.AddSingleton<IConversationContextRetriever>(services =>
{
    var options = services.GetRequiredService<IOptions<SqliteConversationStoreOptions>>().Value;
    return options.Enabled
        ? services.GetRequiredService<HybridConversationContextRetriever>()
        : services.GetRequiredService<NullConversationContextRetriever>();
});
builder.Services.AddSingleton<IPersonalMemoryStore, SqlitePersonalMemoryStore>();
builder.Services.AddSingleton<IConversationStore>(services =>
{
    var options = services.GetRequiredService<IOptions<SqliteConversationStoreOptions>>().Value;
    return options.Enabled
        ? new AuthenticatedConversationStore(
            services.GetRequiredService<SqliteConversationStore>(),
            services.GetRequiredService<InMemoryConversationStore>())
        : services.GetRequiredService<InMemoryConversationStore>();
});
builder.Services.AddSingleton<IToolConfirmationStore, InMemoryToolConfirmationStore>();
builder.Services.AddSingleton<IConversationExecutionLock, InMemoryConversationExecutionLock>();
builder.Services.AddSingleton<IToolAuditSink, InMemoryToolAuditSink>();
builder.Services.AddSingleton<IReminderStore, InMemoryReminderStore>();
builder.Services.AddSingleton<IToolRiskPolicy, DefaultToolRiskPolicy>();
builder.Services.AddSingleton<IInstallationIdentityStore, FileInstallationIdentityStore>();
builder.Services.AddSingleton<IPrivateClientAuthenticationStore>(services =>
{
    var options = services.GetRequiredService<IOptions<PrivateClientOptions>>().Value;
    var databasePath = options.DatabasePath;
    if (string.IsNullOrWhiteSpace(databasePath))
    {
        var installation = services.GetRequiredService<IOptions<InstallationIdentityOptions>>().Value;
        var stateDirectory = installation.StateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAssistant");
        databasePath = Path.Combine(stateDirectory, "private-clients.db");
    }

    return new SqlitePrivateClientAuthenticationStore(databasePath);
});
builder.Services.AddSingleton<PrivateClientAuthenticationService>();
builder.Services.AddSingleton<IAssistantProfileStore, FileAssistantProfileStore>();
builder.Services.AddSingleton<FileStableProfileStores>();
builder.Services.AddSingleton<IUserProfileStore>(services =>
    services.GetRequiredService<FileStableProfileStores>());
builder.Services.AddSingleton<IHouseholdProfileStore>(services =>
    services.GetRequiredService<FileStableProfileStores>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication(LocalApiKeyAuthenticationDefaults.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, LocalApiKeyAuthenticationHandler>(
        LocalApiKeyAuthenticationDefaults.SchemeName,
        _ => { });
builder.Services.AddSingleton<IToolPolicyContextAccessor, HttpContextToolPolicyContextAccessor>();
builder.Services.AddSingleton<ITool, CurrentTimeTool>();
builder.Services.AddSingleton<ITool, TemperatureConversionTool>();
builder.Services.AddSingleton<ITool, CreateReminderTool>();
builder.Services.AddSingleton<ITool, SetAssistantNameTool>();
builder.Services.AddSingleton<ITool, SetUserPreferredNameTool>();
builder.Services.AddSingleton<ITool, SetHouseholdLocationTool>();
builder.Services.AddSingleton<IToolRegistry>(services =>
    new ToolRegistry(services.GetServices<ITool>()));
builder.Services.AddSingleton<FakeLanguageProviderFactory>();
builder.Services.AddHttpClient<OllamaLanguageProvider>();
builder.Services.AddHttpClient<OllamaTextEmbeddingProvider>();
builder.Services.AddSingleton<ITextEmbeddingProvider, OllamaTextEmbeddingProvider>();
builder.Services.AddHttpClient<OllamaConversationIndexSummaryProvider>();
builder.Services.AddSingleton<IConversationIndexSummaryProvider, OllamaConversationIndexSummaryProvider>();
builder.Services.AddHostedService<ConversationIndexingHostedService>();
builder.Services.AddHttpClient<OllamaModelInspector>();
builder.Services.AddSingleton<OllamaModelValidationCache>();
builder.Services.AddScoped<LanguageProviderSelector>();
builder.Services.AddScoped<IConversationOrchestrator, ConversationOrchestrator>();
builder.Services.AddOptions<OrchestrationOptions>()
    .Bind(builder.Configuration.GetSection("LocalAssistant:Orchestration"))
    .Validate(options => options.MaxIterations > 0, "MaxIterations must be greater than zero.")
    .Validate(
        options => options.ProviderTimeout > TimeSpan.Zero && options.ToolTimeout > TimeSpan.Zero && options.ConfirmationTimeout > TimeSpan.Zero,
        "Timeouts must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddOptions<OllamaOptions>()
    .Bind(builder.Configuration.GetSection("LocalAssistant:Ollama"))
    .Validate(
        options => options.ContextWindow > 0,
        "Ollama ContextWindow must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddOptions<LocalIdentityOptions>()
    .Bind(builder.Configuration.GetSection(LocalIdentityOptions.SectionName))
    .Validate(
        options => !options.Enabled ||
            (!string.IsNullOrWhiteSpace(options.PrincipalId) &&
             !string.IsNullOrWhiteSpace(options.ApiKeySha256) &&
             options.ApiKeySha256.Length == 64 &&
             options.ApiKeySha256.All(Uri.IsHexDigit) &&
             options.Scopes is not null &&
             options.Scopes.All(scope => !string.IsNullOrWhiteSpace(scope))),
        "Enabled local identity requires a principal, a SHA-256 API key hash, and non-empty scopes.")
    .ValidateOnStart();
builder.Services.AddOptions<InstallationIdentityOptions>()
    .Bind(builder.Configuration.GetSection(InstallationIdentityOptions.SectionName))
    .Validate(
        options => string.IsNullOrWhiteSpace(options.StateDirectory) ||
            Path.IsPathFullyQualified(options.StateDirectory),
        "Installation state directory must be an absolute path.")
    .ValidateOnStart();
builder.Services.AddOptions<SqliteConversationStoreOptions>()
    .Bind(builder.Configuration.GetSection(SqliteConversationStoreOptions.SectionName))
    .Validate(
        options => string.IsNullOrWhiteSpace(options.DatabasePath) || Path.IsPathFullyQualified(options.DatabasePath),
        "Conversation database path must be an absolute path.")
    .ValidateOnStart();
builder.Services.AddOptions<ConversationRetrievalOptions>()
    .Bind(builder.Configuration.GetSection(ConversationRetrievalOptions.SectionName))
    .Validate(
        options => options.MaximumMatches is > 0 and <= 3 &&
                   options.MaximumContextCharacters is >= 100 and <= 2_000 &&
                   options.IndexingDelay > TimeSpan.Zero &&
                   options.IndexingPollInterval > TimeSpan.Zero,
        "Conversation retrieval limits must be within their supported ranges.")
    .ValidateOnStart();
builder.Services.AddOptions<LocalDocumentSourceOptions>()
    .Bind(builder.Configuration.GetSection(LocalDocumentSourceOptions.SectionName))
    .Validate(
        options => string.IsNullOrWhiteSpace(options.DocumentsRoot) ||
            (Path.IsPathFullyQualified(options.DocumentsRoot) && Directory.Exists(options.DocumentsRoot)),
        "Configured documents root must be an existing absolute directory.")
    .ValidateOnStart();
builder.Services.AddOptions<DocumentSemanticSearchOptions>()
    .Bind(builder.Configuration.GetSection(DocumentSemanticSearchOptions.SectionName))
    .Validate(
        options => options.MinimumSimilarity is >= -1 and <= 1 &&
                   options.MaximumFilesPerSynchronizationCycle > 0 &&
                   options.SynchronizationBudget > TimeSpan.Zero &&
                   options.EmbeddingTimeout > TimeSpan.Zero,
        "Document semantic search limits must be valid.")
    .ValidateOnStart();
builder.Services.AddOptions<PrivateClientOptions>()
    .Bind(builder.Configuration.GetSection(PrivateClientOptions.SectionName))
    .Validate(
        options => string.IsNullOrWhiteSpace(options.DatabasePath) || Path.IsPathFullyQualified(options.DatabasePath),
        "Private client database path must be absolute.")
    .Validate(
        options => options.AdministrativeChallengeLifetime > TimeSpan.Zero && options.SessionLifetime > TimeSpan.Zero,
        "Private client lifetimes must be greater than zero.")
    .ValidateOnStart();

if (bootstrapOwnerRequested)
{
    var bootstrapApplication = builder.Build();
    var configuredIdentity = bootstrapApplication.Services
        .GetRequiredService<IOptions<LocalIdentityOptions>>().Value;
    if (configuredIdentity.Enabled)
    {
        await bootstrapApplication.DisposeAsync();
        throw new InvalidOperationException(
            "Bootstrap requires LocalAssistant:Identity:Enabled to remain disabled.");
    }

    var bootstrapIdentityStore = bootstrapApplication.Services
        .GetRequiredService<IInstallationIdentityStore>();
    var bootstrapResult = await bootstrapIdentityStore.BootstrapAsync(CancellationToken.None);
    await bootstrapApplication.DisposeAsync();
    if (bootstrapResult.Status == InstallationBootstrapStatus.AlreadyInitialized)
    {
        Console.Error.WriteLine("Installation bootstrap was already completed.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("Installation bootstrap completed. Store this API key now; it will not be shown again.");
    Console.WriteLine($"Owner principal: {bootstrapResult.OwnerPrincipalId}");
    Console.WriteLine($"API key: {bootstrapResult.ApiKey}");
    return;
}

if (bootstrapPrivateClientRequested)
{
    if (args.Length > 2 || (args.Length == 2 && !args[1].StartsWith("--display-name=", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            "The --bootstrap-private-client command accepts only an optional --display-name=<name> argument.");
    }

    var displayName = args.Length == 2
        ? args[1]["--display-name=".Length..].Trim()
        : "Local terminal client";
    if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128)
    {
        throw new InvalidOperationException("The private client display name must contain between 1 and 128 characters.");
    }

    var bootstrapApplication = builder.Build();
    var privateClientInstallationIdentity = await bootstrapApplication.Services
        .GetRequiredService<IInstallationIdentityStore>()
        .GetAsync(CancellationToken.None);
    var clientStore = bootstrapApplication.Services.GetRequiredService<IPrivateClientAuthenticationStore>();
    if (privateClientInstallationIdentity is null || await clientStore.HasClientsAsync(CancellationToken.None))
    {
        await bootstrapApplication.DisposeAsync();
        throw new InvalidOperationException(
            "Private client bootstrap requires an existing installation owner and no registered private clients.");
    }

    var authentication = bootstrapApplication.Services.GetRequiredService<PrivateClientAuthenticationService>();
    var challenge = await authentication.CreateAdministrativeChallengeAsync(
        AdministrativeChallengeOperation.CreateClient,
        null,
        TimeSpan.FromMinutes(1),
        CancellationToken.None);
    var credential = await authentication.CompleteClientPairingAsync(
        challenge.Secret,
        privateClientInstallationIdentity.OwnerPrincipalId,
        displayName,
        CancellationToken.None);
    await bootstrapApplication.DisposeAsync();
    if (credential is null)
    {
        throw new InvalidOperationException("Private client bootstrap could not be completed.");
    }

    Console.WriteLine("Private client bootstrap completed. Store this credential now; it will not be shown again.");
    Console.WriteLine($"Client ID: {credential.Client.ClientId}");
    Console.WriteLine($"Credential: {credential.Secret}");
    return;
}

if (createAdministrativeChallengeRequested)
{
    var commandArguments = args.Skip(1).ToArray();
    var operationArgument = commandArguments.SingleOrDefault(argument => argument.StartsWith("--operation=", StringComparison.Ordinal));
    var clientIdArgument = commandArguments.SingleOrDefault(argument => argument.StartsWith("--client-id=", StringComparison.Ordinal));
    if (operationArgument is null || commandArguments.Length is < 1 or > 2 ||
        commandArguments.Any(argument => !ReferenceEquals(argument, operationArgument) && !ReferenceEquals(argument, clientIdArgument)))
    {
        throw new InvalidOperationException(
            "Use --create-administrative-challenge --operation=pair|rotate|revoke [--client-id=<id>].");
    }

    var operation = operationArgument["--operation=".Length..].ToLowerInvariant() switch
    {
        "pair" => AdministrativeChallengeOperation.CreateClient,
        "rotate" => AdministrativeChallengeOperation.RotateCredential,
        "revoke" => AdministrativeChallengeOperation.RevokeClient,
        _ => throw new InvalidOperationException("The administrative operation must be pair, rotate, or revoke."),
    };
    var clientId = clientIdArgument?["--client-id=".Length..];
    if ((operation == AdministrativeChallengeOperation.CreateClient) != string.IsNullOrWhiteSpace(clientId))
    {
        throw new InvalidOperationException("Only rotate and revoke require a client ID.");
    }

    var administrativeApplication = builder.Build();
    var administrativeInstallationIdentity = await administrativeApplication.Services
        .GetRequiredService<IInstallationIdentityStore>()
        .GetAsync(CancellationToken.None);
    var clientStore = administrativeApplication.Services.GetRequiredService<IPrivateClientAuthenticationStore>();
    if (administrativeInstallationIdentity is null || !await clientStore.HasClientsAsync(CancellationToken.None))
    {
        await administrativeApplication.DisposeAsync();
        throw new InvalidOperationException(
            "Administrative challenges require an installation owner and at least one registered private client.");
    }

    if (operation != AdministrativeChallengeOperation.CreateClient &&
        await clientStore.FindActiveClientAsync(clientId!, CancellationToken.None) is null)
    {
        await administrativeApplication.DisposeAsync();
        throw new InvalidOperationException("The requested private client is not active.");
    }

    var options = administrativeApplication.Services.GetRequiredService<IOptions<PrivateClientOptions>>().Value;
    var authentication = administrativeApplication.Services.GetRequiredService<PrivateClientAuthenticationService>();
    var challenge = await authentication.CreateAdministrativeChallengeAsync(
        operation,
        clientId,
        options.AdministrativeChallengeLifetime,
        CancellationToken.None);
    await administrativeApplication.DisposeAsync();
    Console.WriteLine("Administrative challenge created. It will not be shown again.");
    Console.WriteLine($"Operation: {operation}");
    Console.WriteLine($"Expires at UTC: {challenge.Challenge.ExpiresAtUtc:O}");
    Console.WriteLine($"Challenge: {challenge.Secret}");
    return;
}

var app = builder.Build();

var localIdentityOptions = app.Services.GetRequiredService<IOptions<LocalIdentityOptions>>().Value;
var installationIdentityStore = app.Services.GetRequiredService<IInstallationIdentityStore>();
var installationIdentity = await installationIdentityStore.GetAsync(CancellationToken.None);
if (localIdentityOptions.Enabled && installationIdentity is not null)
{
    await app.DisposeAsync();
    throw new InvalidOperationException(
        "Configured local identity and installation bootstrap identity cannot be enabled together.");
}

app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.Request.Headers.ContainsKey(LocalApiKeyAuthenticationDefaults.HeaderName))
    {
        var authentication = await context.AuthenticateAsync(
            LocalApiKeyAuthenticationDefaults.SchemeName);
        if (!authentication.Succeeded)
        {
            await context.ChallengeAsync(LocalApiKeyAuthenticationDefaults.SchemeName);
            return;
        }
    }

    await next();
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapConversationEndpoints();
app.MapDocumentEndpoints();
app.MapPersonalMemoryEndpoints();

app.Run();

public partial class Program;
