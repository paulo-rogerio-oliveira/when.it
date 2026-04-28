namespace DbSense.Contracts.Connections;

public record ConnectionListItem(
    Guid Id,
    string Name,
    string Server,
    string Database,
    string AuthType,
    string? Username,
    string Status,
    DateTime? LastTestedAt,
    string? LastError,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ConnectionDetail(
    Guid Id,
    string Name,
    string Server,
    string Database,
    string AuthType,
    string? Username,
    bool HasPassword,
    string Status,
    DateTime? LastTestedAt,
    string? LastError,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record CreateConnectionRequest(
    string Name,
    string Server,
    string Database,
    string AuthType,
    string? Username,
    string? Password);

public record UpdateConnectionRequest(
    string Name,
    string Server,
    string Database,
    string AuthType,
    string? Username,
    string? Password,
    bool ClearPassword = false);

public record ConnectionTestOutcome(bool Success, string? Error, long ElapsedMs);
