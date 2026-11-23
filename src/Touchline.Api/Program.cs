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

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

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
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<FootballService>();
builder.Services.AddScoped<PlayerIdBridge>();
builder.Services.AddScoped<MatchDetailService>();
builder.Services.AddScoped<StandingsService>();
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
builder.Services.AddScoped<FantasyContextService>();
builder.Services.AddScoped<LeagueService>();
builder.Services.AddScoped<FantasyTeamService>();
builder.Services.AddScoped<SafeWriteService>();
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
builder.Services.AddCors(o => o.AddPolicy(FrontendCors, p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();               // /openapi/v1.json
    app.MapScalarApiReference();    // /scalar
}

app.UseCors(FrontendCors);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program { }
