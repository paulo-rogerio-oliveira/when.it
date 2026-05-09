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
builder.Services.AddScoped<DbSense.Core.RabbitDestinations.IRabbitDestinationsService, DbSense.Core.RabbitDestinations.RabbitDestinationsService>();
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

// Migração idempotente do schema do control DB. Roda só se já está provisionado;
// se ainda não, ProvisioningService.EnsureCreatedAsync cuida da criação inicial.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DbSenseContext>>();
    await using var ctx = await factory.CreateDbContextAsync();
    await DbSense.Core.Persistence.RabbitDestinationsSchemaMigrator.EnsureUpToDateAsync(ctx);
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
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", at = DateTime.UtcNow }));

// Endpoint de diagnóstico: retorna as env vars de configuração que a API recebeu.
// Inclui valor literal + fingerprint sha256:8 pra cruzar com o log do Electron
// (que usa o mesmo hash em [electron] config <name>: source=... fp=<hash>).
// Atenção: expõe secrets em texto plano. Só vale porque a API ouve em localhost.
// Em deploy fora-do-host (atrás de proxy/cluster), remover ou proteger por auth.
app.MapGet("/api/status", () =>
{
    static string Fingerprint(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "EMPTY";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    var envKeys = new[]
    {
        "ConnectionStrings__ControlDb",
        "Security__EncryptionKey",
        "Security__JwtSecret",
        "ASPNETCORE_URLS",
        "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT",
        "RuntimeConfig__Path",
        "CORS_ORIGINS",
    };

    var env = new Dictionary<string, object?>();
    foreach (var k in envKeys)
    {
        var v = Environment.GetEnvironmentVariable(k);
        env[k] = v is null ? null : new { value = v, fp = Fingerprint(v) };
    }

    return Results.Ok(new
    {
        status = "ok",
        at = DateTime.UtcNow,
        process = new
        {
            id = Environment.ProcessId,
            machine = Environment.MachineName,
            user = Environment.UserName,
            cwd = Environment.CurrentDirectory,
            commandLine = Environment.CommandLine,
        },
        env,
    });
});

app.Run();

// Tornar Program acessível ao WebApplicationFactory<Program> dos testes.
public partial class Program;
