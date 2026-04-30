using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DbSense.Core.Reactions;

public class CmdReactionHandler : IReactionHandler
{
    private const int StdOutCaptureMax = 4096;

    private readonly ILogger<CmdReactionHandler> _logger;

    public CmdReactionHandler(ILogger<CmdReactionHandler> logger)
    {
        _logger = logger;
    }

    public string Type => "cmd";

    public async Task<ReactionResult> ExecuteAsync(ReactionContext ctx, CancellationToken ct = default)
    {
        var executable = TryGetString(ctx.Config, "executable");
        if (string.IsNullOrWhiteSpace(executable))
            return new ReactionResult(false, "config.executable ausente.");

        var args = TryGetArray(ctx.Config, "args");
        var sendStdin = TryGetBool(ctx.Config, "send_payload_to_stdin", true);
        var timeoutMs = TryGetInt(ctx.Config, "timeout_ms", 30000);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = sendStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // Variáveis de ambiente derivadas + as do bloco env (se houver)
        psi.EnvironmentVariables["DBSENSE_RULE_ID"] = ctx.RuleId.ToString();
        psi.EnvironmentVariables["DBSENSE_RULE_VERSION"] = ctx.RuleVersion.ToString();
        psi.EnvironmentVariables["DBSENSE_IDEMPOTENCY_KEY"] = ctx.IdempotencyKey;
        if (ctx.Config.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in envEl.EnumerateObject())
                psi.EnvironmentVariables[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        proc.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        try
        {
            if (!proc.Start())
                return new ReactionResult(false, "Process.Start retornou false.");
        }
        catch (Exception ex)
        {
            return new ReactionResult(false, $"Falha ao iniciar processo: {ex.Message}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (sendStdin)
        {
            try
            {
                await proc.StandardInput.WriteAsync(ctx.PayloadJson);
                proc.StandardInput.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao escrever stdin para reaction cmd.");
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return new ReactionResult(
                false,
                $"Timeout após {timeoutMs} ms. stderr: {Truncate(stderr.ToString())}",
                ExitCode: null);
        }

        var combined = $"stdout: {Truncate(stdout.ToString())}\nstderr: {Truncate(stderr.ToString())}";
        return proc.ExitCode == 0
            ? new ReactionResult(true, null, ExitCode: proc.ExitCode)
            : new ReactionResult(false, combined, ExitCode: proc.ExitCode);
    }

    private static void Append(StringBuilder sb, string? data)
    {
        if (data is null) return;
        if (sb.Length >= StdOutCaptureMax) return;
        sb.AppendLine(data);
    }

    private static string Truncate(string s) =>
        s.Length <= StdOutCaptureMax ? s : s[..StdOutCaptureMax] + "…";

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryGetBool(JsonElement el, string name, bool fallback) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    private static int TryGetInt(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : fallback;

    private static List<string> TryGetArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return v.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText())
            .ToList();
    }
}
