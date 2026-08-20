using LocalAssistant.Api.Endpoints;
using LocalAssistant.Api.Fakes;
using LocalAssistant.Api.LanguageModels;
using LocalAssistant.Api.Security;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Orchestration;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
builder.Services.AddSingleton<IToolConfirmationStore, InMemoryToolConfirmationStore>();
builder.Services.AddSingleton<IConversationExecutionLock, InMemoryConversationExecutionLock>();
builder.Services.AddSingleton<IToolAuditSink, InMemoryToolAuditSink>();
builder.Services.AddSingleton<IToolRiskPolicy, DefaultToolRiskPolicy>();
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

var app = builder.Build();

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

app.Run();

public partial class Program;
