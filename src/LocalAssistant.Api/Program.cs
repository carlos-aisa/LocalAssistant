using LocalAssistant.Api.Endpoints;
using LocalAssistant.Api.Fakes;
using LocalAssistant.Core.Conversations;
using LocalAssistant.Core.Orchestration;
using LocalAssistant.Core.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
builder.Services.AddSingleton<ITool, CurrentTimeTool>();
builder.Services.AddSingleton<IToolRegistry>(services =>
    new ToolRegistry(services.GetServices<ITool>()));
builder.Services.AddSingleton<FakeLanguageProviderFactory>();
builder.Services.AddScoped<IConversationOrchestrator, ConversationOrchestrator>();
builder.Services.AddOptions<OrchestrationOptions>()
    .Bind(builder.Configuration.GetSection("LocalAssistant:Orchestration"))
    .Validate(options => options.MaxIterations > 0, "MaxIterations must be greater than zero.")
    .Validate(
        options => options.ProviderTimeout > TimeSpan.Zero && options.ToolTimeout > TimeSpan.Zero,
        "Timeouts must be greater than zero.")
    .ValidateOnStart();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapConversationEndpoints();

app.Run();

public partial class Program;
