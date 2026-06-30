using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using LtlTool.Api.Data;
using LtlTool.Api.Features.Integrations.Alvys;
using LtlTool.Api.Features.Ltl;
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

// Microsoft Entra ID (JWT bearer) authentication.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

// Authorization: require an authenticated user whose email domain is allow-listed.
builder.Services.Configure<AccessPolicyOptions>(builder.Configuration.GetSection("AccessPolicy"));
builder.Services.AddSingleton<IAuthorizationHandler, AllowedEmailDomainHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AccessPolicies.AllowedEmailDomain, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new AllowedEmailDomainRequirement());
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

// CORS for the SPA.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Surface a clear warning when the live source of truth is selected (the default)
// but no credentials are present — live Alvys calls will fail until configured.
{
    var alvys = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlvysOptions>>().Value;
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Alvys");
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
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposed for integration testing (WebApplicationFactory<Program>).
public partial class Program { }
