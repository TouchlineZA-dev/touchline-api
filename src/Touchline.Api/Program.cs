using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Touchline.Api.Data;
using Touchline.Api.Services;
using Touchline.Api.Users;

var builder = WebApplication.CreateBuilder(args);

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
        "ConnectionStrings:Touchline is not set. Locally use `dotnet user-secrets set " +
        "\"ConnectionStrings:Touchline\" \"...\"` in src/Touchline.Api; in hosting set the " +
        "ConnectionStrings__Touchline environment variable. Never commit it.");
}

builder.Services.AddDbContext<TouchlineDbContext>(options => options
    .UseNpgsql(connectionString)
    // Most requests only read. Call AsTracking() or Add/Update explicitly when writing.
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

// Services
builder.Services.Configure<FootballOptions>(builder.Configuration.GetSection(FootballOptions.SectionName));
builder.Services.AddScoped<FootballService>();
builder.Services.AddScoped<GameweekService>();
builder.Services.AddScoped<HealthService>();

// Users
builder.Services.AddSupabaseAuth(builder.Configuration);
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<UserService>();

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
