using DbSense.Core.Persistence;
using DbSense.Core.Security;
using DbSense.Core.Setup;
using DbSense.Core.XEvents;
using DbSense.Worker.Workers;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "DBSENSE_");

var fallbackControlDb = builder.Configuration.GetConnectionString("ControlDb")
    ?? builder.Configuration["CONTROL_DB_CONNECTION"]
    ?? "Server=localhost;Database=dbsense_control;Trusted_Connection=true;TrustServerCertificate=true";

var runtimeConfigPath = builder.Configuration["RuntimeConfig:Path"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "runtime-config.json");

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.AddSingleton<ISecretCipher, SecretCipher>();

builder.Services.AddSingleton<IRuntimeConfigStore>(sp =>
    new FileRuntimeConfigStore(runtimeConfigPath, sp.GetRequiredService<ISecretCipher>()));

builder.Services.AddSingleton<IDbContextFactory<DbSenseContext>>(sp =>
    new DynamicDbContextFactory(sp.GetRequiredService<IRuntimeConfigStore>(), fallbackControlDb));

builder.Services.AddSingleton<IRecordingCollector, RecordingCollector>();

builder.Services.AddHostedService<CommandProcessorWorker>();

var host = builder.Build();
host.Run();
