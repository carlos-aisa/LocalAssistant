using LocalAssistant.Api.Endpoints;
using LocalAssistant.Api.Fakes;
using LocalAssistant.Api.LanguageModels;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Documents;
using LocalAssistant.Core.Orchestration;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.Conversations;
using LocalAssistant.Infrastructure.Documents;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

var bootstrapRequested = args.Any(argument =>
    StringComparer.Ordinal.Equals(argument, "--bootstrap-owner"));
if (bootstrapRequested && args.Length != 1)
{
    throw new InvalidOperationException("The --bootstrap-owner command does not accept additional arguments.");
}

var builder = WebApplication.CreateBuilder(bootstrapRequested ? [] : args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISystemDocumentsPathProvider, SystemDocumentsPathProvider>();
builder.Services.AddSingleton<ILocalDocumentRoot, ConfiguredLocalDocumentRoot>();
builder.Services.AddSingleton<ILocalDocumentSearch, FileSystemDocumentSearch>();
builder.Services.AddSingleton<InMemoryConversationStore>();
builder.Services.AddSingleton<SqliteConversationStore>();
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
builder.Services.AddSingleton<IToolRiskPolicy, DefaultToolRiskPolicy>();
builder.Services.AddSingleton<IInstallationIdentityStore, FileInstallationIdentityStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication(LocalApiKeyAuthenticationDefaults.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, LocalApiKeyAuthenticationHandler>(
        LocalApiKeyAuthenticationDefaults.SchemeName,
        _ => { });
builder.Services.AddSingleton<IToolPolicyContextAccessor, HttpContextToolPolicyContextAccessor>();
builder.Services.AddSingleton<ITool, CurrentTimeTool>();
builder.Services.AddSingleton<ITool, TemperatureConversionTool>();
builder.Services.AddSingleton<IToolRegistry>(services =>
    new ToolRegistry(services.GetServices<ITool>()));
builder.Services.AddSingleton<FakeLanguageProviderFactory>();
builder.Services.AddHttpClient<OllamaLanguageProvider>();
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
builder.Services.AddOptions<LocalDocumentSourceOptions>()
    .Bind(builder.Configuration.GetSection(LocalDocumentSourceOptions.SectionName))
    .Validate(
        options => string.IsNullOrWhiteSpace(options.DocumentsRoot) ||
            (Path.IsPathFullyQualified(options.DocumentsRoot) && Directory.Exists(options.DocumentsRoot)),
        "Configured documents root must be an existing absolute directory.")
    .ValidateOnStart();

if (bootstrapRequested)
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

app.Run();

public partial class Program;
