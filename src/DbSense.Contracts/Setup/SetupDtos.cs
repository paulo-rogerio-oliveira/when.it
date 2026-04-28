namespace DbSense.Contracts.Setup;

public record SetupStatusResponse(string Status, string? SchemaVersion, DateTime? ProvisionedAt);

public record TestConnectionRequest(
    string Server,
    string Database,
    string AuthType,
    string? Username,
    string? Password);

public record TestConnectionResponse(bool Success, string? Error, long ElapsedMs);

public record ProvisionRequest(
    string Server,
    string Database,
    string AuthType,
    string? Username,
    string? Password);

public record ProvisionResponse(
    bool Success,
    string? Error,
    int TablesCreated,
    string SchemaVersion,
    string? ErrorCode = null,
    string? Hint = null);

public record CreateAdminRequest(string Username, string Password);

public record CreateAdminResponse(Guid UserId, string Username);
