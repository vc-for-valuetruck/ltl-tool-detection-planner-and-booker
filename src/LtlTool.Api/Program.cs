using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using LtlTool.Api.Data;
using LtlTool.Api.Options;
using LtlTool.Api.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
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

// CORS for the SPA.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

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
