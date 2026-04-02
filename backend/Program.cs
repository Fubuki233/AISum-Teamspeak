using Microsoft.AspNetCore.DataProtection;
using TsAi.Components;
using TsAi.Services;

var builder = WebApplication.CreateBuilder(args);

// Razor / Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Log the full exception when a Blazor circuit throws an unhandled error
        options.DetailedErrors = true;
    });

// --- TS REST API client ---
builder.Services.AddHttpClient<TsApiClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration["TsApi:BaseUrl"] ?? "http://localhost:8080/");
    var key = builder.Configuration["TsApi:ApiKey"];
    if (!string.IsNullOrEmpty(key))
        http.DefaultRequestHeaders.Add("X-Api-Key", key);
});

// --- 字节跳动 豆包 LLM client ---
builder.Services.AddHttpClient<ByteDanceAiService>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration["Doubao:BaseUrl"] ?? "https://ark.cn-beijing.volces.com/api/v3/");
    var key = builder.Configuration["Doubao:ApiKey"];
    if (!string.IsNullOrEmpty(key))
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
});

// --- Data Protection: persist keys so antiforgery tokens survive restarts ---
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new System.IO.DirectoryInfo("/app/keys"));

// --- Singletons ---
builder.Services.AddSingleton<AiSttService>();
builder.Services.AddSingleton<UserMappingService>();
builder.Services.AddSingleton<AiRuntimeOptions>();
builder.Services.AddSingleton<ChatHistoryStore>();
builder.Services.AddSingleton<ChatSessionManager>();
builder.Services.AddSingleton<ClientRegistry>();

// --- Background services ---
builder.Services.AddHostedService<VoiceReceiver>();

// --- Scoped (per Blazor circuit) ---
builder.Services.AddScoped<CurrentUser>();

var app = builder.Build();

// Log unhandled Blazor circuit exceptions with full stack trace
app.Use(async (ctx, next) =>
{
    try { await next(ctx); }
    catch (Exception ex)
    {
        var log = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        log.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        throw;
    }
});

// Start the central orchestrator
app.Services.GetRequiredService<ChatSessionManager>().Start();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
