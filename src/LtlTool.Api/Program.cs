using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using LtlTool.Api.Data;
using LtlTool.Api.Features.Ai;
using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Integrations.Yard;
using LtlTool.Api.Features.Integrations.Yard.Webhooks;
using LtlTool.Api.Features.Ltl;
using LtlTool.Api.Features.Ltl.Agents;
using LtlTool.Api.Options;
using LtlTool.Api.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Authentication schemes. Both are always registered so the auth pipeline is stable
// across environments; the effective default is selected at request time from
// AccessPolicy:Mode via a ForwardDefaultSelector. This lets tests and container-restart
// scenarios switch modes without a rebuild, and it defers config reads until after the
// full config chain (env vars + in-memory + appsettings) is composed — avoiding the
// build-time read bug where WebApplicationFactory in-memory sources arrive too late.
builder.Services.Configure<AccessPolicyOptions>(builder.Configuration.GetSection("AccessPolicy"));

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = AuthenticationSchemeRouter.RouterScheme;
        options.DefaultChallengeScheme = AuthenticationSchemeRouter.RouterScheme;
    })
    // Router that reads AccessPolicy:Mode at request time and forwards to the right
    // concrete scheme. Kept separate from the concrete handlers so both stay
    // independently testable.
    .AddPolicyScheme(AuthenticationSchemeRouter.RouterScheme, "AccessPolicy-selected", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var opts = context.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<AccessPolicyOptions>>().Value;
            return opts.Mode == AccessPolicyMode.Demo
                ? DemoAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
        };
    })
    // Demo-mode handler. Present in every build; only reached when the router selects it.
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DemoAuthenticationHandler>(
        DemoAuthenticationHandler.SchemeName, _ => { })
    // Microsoft Entra ID (JWT bearer). Only reached when the router selects it, so tests
    // that run in Demo mode never trigger MSAL's ClientId validation.
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

// Authorization: require an authenticated user whose email domain is allow-listed.
builder.Services.AddSingleton<IAuthorizationHandler, AllowedEmailDomainHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AccessPolicies.AllowedEmailDomain, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new AllowedEmailDomainRequirement());
    })
    // Service-to-service policy for the Yard→LTL ingestion endpoint. Satisfied by the Yard managed
    // identity's Entra app role / scope (not an email domain). The handler is registered in
    // AddYardIngestion; see YardEventIngestHandler for the Demo-mode / disabled-check escape hatches.
    .AddPolicy(AccessPolicies.YardEventIngest, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new LtlTool.Api.Features.Ltl.YardIngestion.YardEventIngestRequirement());
    })
    .SetDefaultPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new AllowedEmailDomainRequirement())
        .Build());

// Database (SQL Server).
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Optional outbound external API placeholder. Replace/extend in your application.
builder.Services.Configure<ExternalApiOptions>(builder.Configuration.GetSection("ExternalApi"));
builder.Services.AddHttpClient("ExternalApi", (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ExternalApiOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
    {
        client.BaseAddress = new Uri(opts.BaseUrl);
    }
});

// Alvys TMS integration. Live Alvys is the default source of truth for LTL data;
// credentials stay server-side and are never exposed to the Angular SPA.
builder.Services.AddAlvysIntegration(builder.Configuration);

// LTL decision-support layer (normalization, billing readiness, match scoring, search,
// internal assignment audit) on top of the read-only Alvys integration.
builder.Services.AddLtlDecisionSupport(builder.Configuration);

// Read-only background agents (opportunity + exception sweepers, AR digest) with a durable,
// honest heartbeat. Flag-gated OFF by default under Ltl:Agents:*:Enabled; every agent reuses
// existing Alvys reads and the internal notification store, and none writes to Alvys.
builder.Services.AddLtlAgents(builder.Configuration);

// AI narrative HTTP surface (Phase 2 · Sprint 1, #149/#150). Binds AI options and registers the
// real NarrativeService (Azure OpenAI, DefaultAzureCredential, 10-min in-memory cache) when
// AI:NarrativeEnabled=true, else a fail-closed NullNarrativeService. Kill-switched off by default
// and read-only against Alvys — no writeback path, no EF DbSet.
builder.Services.AddAiNarrative(builder.Configuration);

// Yard boundary integration (issue #166): read-only presence client folded into assignment validation
// and the dock, plus the HMAC-verified inbound webhook receiver. The Yard is a peer system, never a
// source of operational truth — Alvys stays authoritative. Degrades honestly when unconfigured.
builder.Services.AddYardIntegration(builder.Configuration);

// SignalR backs the Yard→dock real-time fan-out (load released / new opportunity). Backend-only; the
// SPA refreshes over REST, so no browser SignalR client dependency is required to keep the UI honest.
builder.Services.AddSignalR();

// CORS for the SPA.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Apply pending EF Core migrations on startup so the schema (e.g. dispatcher saved views) is ready.
// Skipped under the "Testing" environment, where integration tests boot the app without a database.
// Made resilient: a SQL outage or firewall drop must not prevent Kestrel from listening.
// The app's live-Alvys read paths do not require the DB; saved views degrade gracefully.
if (!app.Environment.IsEnvironment("Testing"))
{
    try
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    }
    catch (Exception migrateEx)
    {
        // Log loudly but keep serving. Health endpoint stays green; saved-views endpoints
        // may 500 individually until the DB comes back, which is honest and expected.
        var startupLoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        startupLoggerFactory.CreateLogger("Startup").LogError(
            migrateEx,
            "EF Core migration failed at startup. API will continue serving; DB-backed endpoints may fail until connectivity is restored.");
    }
}

// Surface a clear warning when the live source of truth is selected (the default)
// but no credentials are present — live Alvys calls will fail until configured.
{
    var alvys = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlvysOptions>>().Value;
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Alvys");

    // Loud, unmistakable demo-mode banner. If this line ever appears in a UAT or prod
    // deployment, someone shipped the wrong config: every request in demo mode is granted
    // full API access under a synthetic identity.
    var effectiveAccessPolicy = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<AccessPolicyOptions>>().Value;
    if (effectiveAccessPolicy.Mode == AccessPolicyMode.Demo)
    {
        var demoLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AccessPolicy");
        demoLogger.LogWarning(
            "\n" +
            "================================================================\n" +
            "  DEMO AUTH MODE ENABLED - every request is authenticated as\n" +
            "  demo@valuetruck.com with a synthetic identity. Never deploy\n" +
            "  this configuration to a public-facing environment.\n" +
            "================================================================");
    }
    if (alvys.Provider == AlvysProvider.Live && !alvys.HasCredentials)
    {
        startupLogger.LogWarning(
            "Alvys provider is Live (default source of truth) but no credentials are configured. " +
            "Set Alvys:ClientId / Alvys:ClientSecret server-side, or select the Fallback provider for local/UAT.");
    }
    else
    {
        startupLogger.LogInformation("Alvys provider configured: {Provider}.", alvys.Provider);
    }

    var writeback = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<LtlTool.Api.Features.Integrations.Alvys.Writeback.AlvysWriteOptions>>()
        .Value;
    startupLogger.LogInformation(
        "Alvys writeback mode: {Mode} (no live Alvys mutation is performed in this phase).",
        writeback.Mode);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Belt-and-braces: any uncaught exception on any endpoint returns a small, honest ProblemDetails
// body instead of a bare 500 with an empty response. Callers (SPA / integrations) already expect
// error bodies of the shape { error: string }; the ProblemDetails writer here emits that shape's
// superset so their existing error handlers keep working. Alvys credentials / payloads are never
// echoed — only the exception type and a stable request id.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    var log = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("UnhandledException");
    log?.LogError(ex, "Unhandled exception on {Method} {Path} — returning 500 ProblemDetails.",
        context.Request.Method, context.Request.Path);

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";
    var problem = new
    {
        type = "about:blank",
        title = "Unexpected server error",
        status = 500,
        // Kept generic on purpose so we never leak Alvys payloads / stack traces to clients.
        // The correlated log line above carries the actual exception type + message server-side.
        error = "Something went wrong on the server. The dispatcher can retry safely — nothing was written to Alvys.",
        traceId = context.TraceIdentifier,
    };
    await System.Text.Json.JsonSerializer.SerializeAsync(
        context.Response.Body, problem, options: (System.Text.Json.JsonSerializerOptions?)null, cancellationToken: context.RequestAborted);
}));

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<YardEventsHub>(YardEventsHub.Path);

app.Run();

// Exposed for integration testing (WebApplicationFactory<Program>).
public partial class Program { }
