using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Scalar.AspNetCore;
using Touchline.Api;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Services;
using Touchline.Api.Users;

var builder = WebApplication.CreateBuilder(args);

// Local development: settings such as ConnectionStrings__Touchline from .env (gitignored).
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddDotEnv();
}

// Staging and Production refuse to start with incomplete or unsafe configuration.
StartupValidation.ThrowIfInvalid(builder.Configuration, builder.Environment);

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    // A request the API can't read (missing field, wrong type, bad JSON) gets the same shape as every other
    // 400: a machine-readable code, "errors" as a list of messages, and "violations" naming each field.
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = Touchline.Api.Controllers.InvalidRequest.Create);
builder.Services.AddProblemDetails();
builder.Services.AddTouchlineOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<OperationsOptions>(builder.Configuration.GetSection(OperationsOptions.SectionName));
builder.Services.AddTouchlineRateLimits(builder.Configuration);
builder.Services.AddSingleton<AdminAuditFilter>();

// Database
var connectionString = builder.Configuration.GetConnectionString("Touchline");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Touchline is not set. Locally copy .env.example to .env at the repo " +
        "root and fill in ConnectionStrings__Touchline (or use `dotnet user-secrets`); in " +
        "hosting set the ConnectionStrings__Touchline environment variable. Never commit it.");
}

// Supabase's transaction pooler (port 6543) hangs on the DISCARD ALL that Npgsql sends when it
// reuses a pooled connection, so every second query timed out. With transaction pooling there is no
// session state to reset anyway, so turn the reset off.
var npgsql = new NpgsqlConnectionStringBuilder(connectionString) { NoResetOnClose = true };

builder.Services.AddDbContext<TouchlineDbContext>(options => options
    .UseNpgsql(npgsql.ConnectionString)
    // Most requests only read. Call AsTracking() or Add/Update explicitly when writing.
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

// Services
builder.Services.Configure<FootballOptions>(builder.Configuration.GetSection(FootballOptions.SectionName));
builder.Services.AddSingleton<SchemaFeatures>();
builder.Services.AddScoped<FootballReads>();
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<FootballService>();
builder.Services.AddScoped<PlayerIdBridge>();
builder.Services.AddScoped<MatchDetailService>();
builder.Services.AddScoped<StandingsService>();
builder.Services.AddScoped<FootballDetailService>();
builder.Services.AddScoped<StagingKitService>();
builder.Services.Configure<StagingKitOptions>(builder.Configuration.GetSection(StagingKitOptions.SectionName));
builder.Services.Configure<FreshnessOptions>(builder.Configuration.GetSection(FreshnessOptions.SectionName));
builder.Services.AddScoped<GameweekService>();
builder.Services.AddScoped<HealthService>();

// Fantasy: every rule number lives in the "Fantasy" section (Fantasy/FantasyRules.cs).
builder.Services.Configure<FantasyRules>(builder.Configuration.GetSection(FantasyRules.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<FantasyDataService>();
builder.Services.AddScoped<SquadStore>();
builder.Services.AddScoped<MatchFactsService>();
builder.Services.AddScoped<PointsService>();
builder.Services.AddScoped<SettlementService>();
builder.Services.Configure<SettlementOptions>(builder.Configuration.GetSection(SettlementOptions.SectionName));
builder.Services.AddScoped<FantasyContextService>();
builder.Services.AddScoped<LeagueService>();
builder.Services.AddScoped<FantasyTeamService>();
builder.Services.AddScoped<SafeWriteService>();
builder.Services.AddScoped<DraftService>();
builder.Services.Configure<DraftOptions>(builder.Configuration.GetSection(DraftOptions.SectionName));
builder.Services.AddScoped<WizardService>();
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<AdminService>();

// Users
builder.Services.AddSupabaseAuth(builder.Configuration);
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<AccountService>();
builder.Services.Configure<AccountOptions>(builder.Configuration.GetSection(AccountOptions.SectionName));

const string FrontendCors = "frontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
// Origins come from per-environment configuration (Cors:AllowedOrigins). ETag is exposed so browser
// clients can read the squad version and send it back in If-Match.
builder.Services.AddCors(o => o.AddPolicy(FrontendCors, p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("ETag")));

var app = builder.Build();

// The effective budget, after appsettings and environment variables (Fantasy__Budget), so a deploy
// running with the wrong budget shows up in the first log lines.
app.Logger.LogInformation("Fantasy budget: {Budget}",
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FantasyRules>>().Value.Budget);

app.UseExceptionHandler();
app.UseStatusCodePages();

// The OpenAPI document is public contract information (docs/api/openapi-v1.json is exported from it).
// Served in Development and Testing, or anywhere OpenApi:Expose is true. The Scalar UI is Development only.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing") || builder.Configuration.GetValue<bool>("OpenApi:Expose"))
{
    app.MapOpenApi();               // /openapi/v1.json
}
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();    // /scalar
}

// Size limit: refuse oversized bodies before they reach model binding (413).
var maxBody = builder.Configuration.GetSection(OperationsOptions.SectionName).Get<OperationsOptions>()?.MaxRequestBodyBytes ?? 64 * 1024;
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength > maxBody)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { title = "Request too large", status = 413, code = "request_too_large" });
        return;
    }
    await next();
});

app.UseCors(FrontendCors);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
