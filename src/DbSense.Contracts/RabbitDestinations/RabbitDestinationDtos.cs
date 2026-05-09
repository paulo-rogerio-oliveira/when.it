namespace DbSense.Contracts.RabbitDestinations;

public record RabbitDestinationListItem(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string VirtualHost,
    string Username,
    bool UseTls,
    string DefaultExchange,
    string Status,
    DateTime? LastTestedAt,
    string? LastError,
    DateTime CreatedAt);

public record RabbitDestinationDetail(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string VirtualHost,
    string Username,
    bool HasPassword,
    bool UseTls,
    string DefaultExchange,
    string Status,
    DateTime? LastTestedAt,
    string? LastError,
    DateTime CreatedAt);

public record CreateRabbitDestinationRequest(
    string Name,
    string Host,
    int Port,
    string VirtualHost,
    string Username,
    string? Password,
    bool UseTls,
    string DefaultExchange);

public record UpdateRabbitDestinationRequest(
    string Name,
    string Host,
    int Port,
    string VirtualHost,
    string Username,
    string? Password,
    bool UseTls,
    string DefaultExchange,
    bool ClearPassword = false);

public record RabbitDestinationTestOutcome(bool Success, string? Error, long ElapsedMs);
