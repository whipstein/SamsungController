using SamsungController.Automation.Macros;
using SamsungController.Core.Connection;

namespace SamsungController.Web.Services;

public sealed record TvConnectionRequest(
    string DisplayName,
    string Host,
    bool Secure,
    int? Port,
    bool AllowUntrustedCertificate = true);

public sealed record ControllerSnapshot(
    string DisplayName,
    string? Host,
    string ApplicationName,
    bool Secure,
    int? Port,
    string MacroFilePath,
    SamsungConnectionState ConnectionState,
    long ConnectionGeneration,
    bool HasToken,
    string ProtocolLogPath,
    string? LastError,
    bool MacroRunning,
    string? ActiveMacro,
    string? LastMacroStatus);

public sealed record MacroSummary(
    string Name,
    string? Description,
    int StepCount);

public sealed record MacroRunSnapshot(
    bool IsRunning,
    string? ActiveMacro,
    string? Status,
    IReadOnlyList<MacroExecutionProgress> Progress);
