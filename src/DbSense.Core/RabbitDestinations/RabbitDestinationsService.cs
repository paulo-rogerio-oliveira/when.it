using System.Diagnostics;
using DbSense.Core.Domain;
using DbSense.Core.Persistence;
using DbSense.Core.Security;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace DbSense.Core.RabbitDestinations;

public interface IRabbitDestinationsService
{
    Task<IReadOnlyList<RabbitMqDestination>> ListAsync(CancellationToken ct = default);
    Task<RabbitMqDestination?> GetAsync(Guid id, CancellationToken ct = default);
    Task<RabbitMqDestination> CreateAsync(
        string name, string host, int port, string virtualHost, string username,
        string? password, bool useTls, string defaultExchange, CancellationToken ct = default);
    Task<RabbitMqDestination?> UpdateAsync(
        Guid id, string name, string host, int port, string virtualHost, string username,
        string? password, bool useTls, string defaultExchange, bool clearPassword,
        CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<RabbitDestinationTestResult> TestAsync(Guid id, CancellationToken ct = default);
    Task<RabbitDestinationTestResult> TestAdHocAsync(
        string host, int port, string virtualHost, string username,
        string? password, bool useTls, CancellationToken ct = default);
}

public record RabbitDestinationTestResult(bool Success, string? Error, long ElapsedMs);

public class RabbitDestinationsService : IRabbitDestinationsService
{
    // Test endpoint usa ConnectionFactory direta (não passa pelo IRabbitConnectionPool).
    // Razão: o pool é singleton no Worker e cacheia conexões por destination.Id —
    // testar via pool poluiria o cache com tentativas falhas. Test sempre faz handshake fresh.
    private static readonly TimeSpan TestHandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<DbSenseContext> _contextFactory;
    private readonly ISecretCipher _cipher;

    public RabbitDestinationsService(IDbContextFactory<DbSenseContext> contextFactory, ISecretCipher cipher)
    {
        _contextFactory = contextFactory;
        _cipher = cipher;
    }

    public async Task<IReadOnlyList<RabbitMqDestination>> ListAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        return await ctx.RabbitMqDestinations.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<RabbitMqDestination?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        return await ctx.RabbitMqDestinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task<RabbitMqDestination> CreateAsync(
        string name, string host, int port, string virtualHost, string username,
        string? password, bool useTls, string defaultExchange, CancellationToken ct = default)
    {
        Validate(name, host, port, virtualHost, username, password, isUpdate: false);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var dest = new RabbitMqDestination
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Host = host.Trim(),
            Port = port,
            VirtualHost = string.IsNullOrWhiteSpace(virtualHost) ? "/" : virtualHost.Trim(),
            Username = username.Trim(),
            PasswordEncrypted = string.IsNullOrEmpty(password) ? Array.Empty<byte>() : _cipher.Encrypt(password),
            UseTls = useTls,
            DefaultExchange = defaultExchange?.Trim() ?? string.Empty,
            Status = "inactive",
            CreatedAt = DateTime.UtcNow
        };
        ctx.RabbitMqDestinations.Add(dest);
        await ctx.SaveChangesAsync(ct);
        return dest;
    }

    public async Task<RabbitMqDestination?> UpdateAsync(
        Guid id, string name, string host, int port, string virtualHost, string username,
        string? password, bool useTls, string defaultExchange, bool clearPassword,
        CancellationToken ct = default)
    {
        Validate(name, host, port, virtualHost, username, password, isUpdate: true);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var dest = await ctx.RabbitMqDestinations.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dest is null) return null;

        dest.Name = name.Trim();
        dest.Host = host.Trim();
        dest.Port = port;
        dest.VirtualHost = string.IsNullOrWhiteSpace(virtualHost) ? "/" : virtualHost.Trim();
        dest.Username = username.Trim();
        if (!string.IsNullOrEmpty(password))
            dest.PasswordEncrypted = _cipher.Encrypt(password);
        else if (clearPassword)
            dest.PasswordEncrypted = Array.Empty<byte>();
        dest.UseTls = useTls;
        dest.DefaultExchange = defaultExchange?.Trim() ?? string.Empty;
        await ctx.SaveChangesAsync(ct);
        return dest;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var dest = await ctx.RabbitMqDestinations.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dest is null) return false;
        ctx.RabbitMqDestinations.Remove(dest);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<RabbitDestinationTestResult> TestAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
        var dest = await ctx.RabbitMqDestinations.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dest is null) return new RabbitDestinationTestResult(false, "Destino não encontrado.", 0);

        var password = dest.PasswordEncrypted is { Length: > 0 } ? _cipher.Decrypt(dest.PasswordEncrypted) : null;
        var result = await TryConnectAsync(
            dest.Host, dest.Port, dest.VirtualHost, dest.Username, password, dest.UseTls, ct);

        dest.LastTestedAt = DateTime.UtcNow;
        dest.LastError = result.Success ? null : result.Error;
        dest.Status = result.Success ? "active" : "error";
        await ctx.SaveChangesAsync(ct);
        return result;
    }

    public Task<RabbitDestinationTestResult> TestAdHocAsync(
        string host, int port, string virtualHost, string username,
        string? password, bool useTls, CancellationToken ct = default)
        => TryConnectAsync(host, port, virtualHost, username, password, useTls, ct);

    private static async Task<RabbitDestinationTestResult> TryConnectAsync(
        string host, int port, string virtualHost, string username,
        string? password, bool useTls, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = host,
                Port = port > 0 ? port : (useTls ? 5671 : 5672),
                VirtualHost = string.IsNullOrEmpty(virtualHost) ? "/" : virtualHost,
                UserName = username,
                Password = password ?? string.Empty,
                Ssl = useTls
                    ? new SslOption { Enabled = true, ServerName = host }
                    : new SslOption { Enabled = false },
                RequestedConnectionTimeout = TestHandshakeTimeout,
                SocketReadTimeout = TestHandshakeTimeout,
                SocketWriteTimeout = TestHandshakeTimeout,
                AutomaticRecoveryEnabled = false,
                ClientProvidedName = "DbSense.Api:test"
            };

            // CreateConnection é síncrono no client 6.x — Task.Run pra não bloquear request thread.
            await Task.Run(() =>
            {
                using var conn = factory.CreateConnection();
                using var ch = conn.CreateModel();
                // Não declara nada — só prova handshake + canal abertos.
            }, ct);

            sw.Stop();
            return new RabbitDestinationTestResult(true, null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RabbitDestinationTestResult(false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static void Validate(
        string name, string host, int port, string virtualHost, string username,
        string? password, bool isUpdate)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nome obrigatório.", nameof(name));
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host obrigatório.", nameof(host));
        if (port < 0 || port > 65535) throw new ArgumentException("Port fora do range 0..65535.", nameof(port));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username obrigatório.", nameof(username));
        if (!isUpdate && string.IsNullOrEmpty(password))
            throw new ArgumentException("Password obrigatório no cadastro inicial.", nameof(password));
        _ = virtualHost; // pode ser vazio (vira "/")
    }
}
