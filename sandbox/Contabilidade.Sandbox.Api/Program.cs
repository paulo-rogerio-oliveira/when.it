using Contabilidade.Sandbox.Api.Data;
using Contabilidade.Sandbox.Api.Endpoints;
using Contabilidade.Sandbox.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default não configurada.");

builder.Services.AddDbContext<ContabilidadeContext>(opt =>
    opt.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
builder.Services.AddScoped<SaldoService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                  ?? new[] { "http://localhost:5173" };
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.ConfigureHttpJsonOptions(opt =>
{
    opt.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    opt.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

app.UseCors();

// Seed na inicialização (idempotente).
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<ContabilidadeContext>();
    await DatabaseSeeder.SeedAsync(ctx);
}

app.MapGet("/", () => Results.Ok(new { name = "Contabilidade Sandbox API", status = "ok" }));
app.MapEmpresaEndpoints();
app.MapPlanoContaEndpoints();
app.MapLancamentoEndpoints();
app.MapSaldoEndpoints();

app.Run();
