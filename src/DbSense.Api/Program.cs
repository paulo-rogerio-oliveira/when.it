using DbSense.Core.Auth;
using DbSense.Core.Persistence;
using DbSense.Core.Security;
using DbSense.Core.Setup;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Config from env: DBSENSE_* maps via __ double underscore convention.
builder.Configuration.AddEnvironmentVariables(prefix: "DBSENSE_");

var connectionString = builder.Configuration.GetConnectionString("ControlDb")
    ?? builder.Configuration["CONTROL_DB_CONNECTION"]
    ?? "Server=localhost;Database=dbsense_control;Trusted_Connection=true;TrustServerCertificate=true";

var runtimeConfigPath = builder.Configuration["RuntimeConfig:Path"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "runtime-config.json");

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.AddSingleton<ISecretCipher, SecretCipher>();
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<IJwtService, JwtService>();

builder.Services.AddSingleton<IRuntimeConfigStore>(sp =>
    new FileRuntimeConfigStore(runtimeConfigPath, sp.GetRequiredService<ISecretCipher>()));

builder.Services.AddSingleton<IDbContextFactory<DbSenseContext>>(sp =>
    new DynamicDbContextFactory(sp.GetRequiredService<IRuntimeConfigStore>(), connectionString));

builder.Services.AddScoped<IProvisioningService, ProvisioningService>();
builder.Services.AddScoped<IAdminUserService, AdminUserService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<DbSense.Core.Connections.IConnectionsService, DbSense.Core.Connections.ConnectionsService>();
builder.Services.AddScoped<DbSense.Core.Recordings.IRecordingsService, DbSense.Core.Recordings.RecordingsService>();
builder.Services.AddScoped<DbSense.Core.Inference.IInferenceService, DbSense.Core.Inference.InferenceService>();
builder.Services.AddScoped<DbSense.Core.Rules.IRulesService, DbSense.Core.Rules.RulesService>();
builder.Services.AddScoped<DbSense.Core.Reactions.IOutboxEnqueuer, DbSense.Core.Reactions.OutboxEnqueuer>();

builder.Services.Configure<DbSense.Core.Inference.LlmOptions>(
    builder.Configuration.GetSection(DbSense.Core.Inference.LlmOptions.SectionName));
builder.Services.AddHttpClient<DbSense.Core.Inference.IAnthropicClient, DbSense.Core.Inference.AnthropicClient>();
builder.Services.AddScoped<DbSense.Core.Inference.ILlmInferenceService, DbSense.Core.Inference.LlmInferenceService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<JwtBearerOptions>,
    DbSense.Api.Infrastructure.JwtBearerPostConfigure>();
builder.Services.AddAuthorization();

var corsOrigins = builder.Configuration["CORS_ORIGINS"]?.Split(',', StringSplitOptions.RemoveEmptyEntries)
    ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

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
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", at = DateTime.UtcNow }));

app.Run();

// Tornar Program acessível ao WebApplicationFactory<Program> dos testes.
public partial class Program;
