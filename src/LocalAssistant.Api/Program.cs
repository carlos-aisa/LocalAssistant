using LocalAssistant.Api.Endpoints;
using LocalAssistant.Api.Fakes;
using LocalAssistant.Api.LanguageModels;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Orchestration;
using LocalAssistant.Core.Security.ToolRisk;
using LocalAssistant.Core.Tools;
using LocalAssistant.Infrastructure.LanguageModels.Ollama;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
builder.Services.AddSingleton<IToolConfirmationStore, InMemoryToolConfirmationStore>();
builder.Services.AddSingleton<IConversationExecutionLock, InMemoryConversationExecutionLock>();
builder.Services.AddSingleton<IToolRiskPolicy, DefaultToolRiskPolicy>();
builder.Services.AddSingleton<IToolPolicyContextAccessor, AnonymousToolPolicyContextAccessor>();
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

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapConversationEndpoints();

app.Run();

public partial class Program;
