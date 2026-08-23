using SamsungController.Automation.Macros;
using SamsungController.Automation.Navigation;
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
    string? LastMacroStatus,
    bool NavigationRunning,
    string? MenuPath,
    MenuStateConfidence MenuConfidence);

public sealed record MacroSummary(
    string Name,
    string? Description,
    int StepCount);

public sealed record MacroRunSnapshot(
    bool IsRunning,
    string? ActiveMacro,
    string? Status,
    IReadOnlyList<MacroExecutionProgress> Progress);

public sealed record MenuNodeSummary(
    string Id,
    string Label,
    string Path,
    int Depth,
    string? Description,
    bool HasVerifiedRoute,
    bool HasDraftRoute);

public sealed record MenuAnchorSummary(
    string Id,
    string Label,
    string TargetPath,
    string? Description,
    bool Verified,
    int CommandCount);

public sealed record MenuNavigationSnapshot(
    string DefinitionPath,
    string? DefinitionName,
    string? Model,
    MenuDefinitionContext? Context,
    IReadOnlyList<MenuNodeSummary> Nodes,
    IReadOnlyList<MenuAnchorSummary> Anchors,
    MenuState State,
    NavigationPlan? Plan,
    bool IsRunning,
    string? Status,
    NavigationProgress? Progress,
    string? Error);
