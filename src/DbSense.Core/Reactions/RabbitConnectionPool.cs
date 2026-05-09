using System.Collections.Concurrent;
using DbSense.Core.Domain;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace DbSense.Core.Reactions;

public interface IRabbitConnectionPool
{
    // Retorna uma IConnection viva pro destination informado. Reusa a do cache se ainda
    // está aberta; recria se foi fechada (broker reiniciado, network drop com
    // AutomaticRecoveryEnabled falhou). A senha já vem decriptada — o pool não acessa
    // ISecretCipher diretamente.
    IConnection GetOrCreate(RabbitMqDestination destination, string decryptedPassword);
}

// Singleton. Mantém uma IConnection por destination_id reutilizada entre publicações
// (o handshake AMQP é caro, channels são leves — criar 1 channel por publish é OK).
// IConnection é thread-safe. IModel (channel) NÃO é — quem usa o pool cria seu próprio
// channel a cada execução.
public class RabbitConnectionPool : IRabbitConnectionPool, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<IConnection>> _connections = new();
    private readonly ILogger<RabbitConnectionPool> _logger;

    public RabbitConnectionPool(ILogger<RabbitConnectionPool> logger)
    {
        _logger = logger;
    }

    public IConnection GetOrCreate(RabbitMqDestination destination, string decryptedPassword)
    {
        // Hot path: cache hit + connection viva.
        if (_connections.TryGetValue(destination.Id, out var cached)
            && cached.IsValueCreated && cached.Value.IsOpen)
        {
            return cached.Value;
        }

        // Slow path: recicla a entrada (se houver) e cria nova.
        // O Lazy<> evita corrida quando 2 threads tentam criar simultaneamente.
        var fresh = new Lazy<IConnection>(() => Build(destination, decryptedPassword));
        _connections.AddOrUpdate(destination.Id,
            _ => fresh,
            (_, existing) =>
            {
                if (existing.IsValueCreated && existing.Value.IsOpen)
                    return existing; // outra thread já recriou
                if (existing.IsValueCreated)
                {
                    try { existing.Value.Dispose(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Falha ao descartar conexão Rabbit antiga ({Id}).", destination.Id); }
                }
                return fresh;
            });

        return _connections[destination.Id].Value;
    }

    private IConnection Build(RabbitMqDestination dest, string password)
    {
        var factory = new ConnectionFactory
        {
            HostName = dest.Host,
            Port = dest.Port > 0 ? dest.Port : (dest.UseTls ? 5671 : 5672),
            VirtualHost = string.IsNullOrEmpty(dest.VirtualHost) ? "/" : dest.VirtualHost,
            UserName = dest.Username,
            Password = password,
            Ssl = dest.UseTls
                ? new SslOption { Enabled = true, ServerName = dest.Host }
                : new SslOption { Enabled = false },
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
            TopologyRecoveryEnabled = true,
            ClientProvidedName = $"DbSense.Worker:{dest.Name}"
        };

        _logger.LogInformation("Abrindo conexão RabbitMQ {Host}:{Port}/{VHost} (dest={Name}).",
            factory.HostName, factory.Port, factory.VirtualHost, dest.Name);

        return factory.CreateConnection();
    }

    public void Dispose()
    {
        foreach (var lazy in _connections.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try { lazy.Value.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Falha ao fechar conexão Rabbit no shutdown."); }
        }
        _connections.Clear();
    }
}
