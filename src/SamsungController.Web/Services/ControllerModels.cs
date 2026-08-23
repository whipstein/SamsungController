using SamsungController.Automation.Macros;
using SamsungController.Automation.Navigation;
using SamsungController.Core.Connection;
using SamsungController.Core.Protocol;

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

public enum MenuAuthoringItemKind
{
    Anchor,
    Transition
}

public sealed record MenuDefinitionCreationRequest(
    string Id,
    string Name,
    string Model,
    string Firmware,
    string Signal,
    string PictureMode,
    string Input);

public sealed record MenuRecordingRequest(
    MenuAuthoringItemKind Kind,
    string ItemId,
    string Label,
    string? SourceNodeId,
    string TargetNodeId,
    string? NewTargetLabel,
    string? NewTargetParentId,
    int ReplayDelayMilliseconds = 150);

public sealed record MenuRecordedStepSummary(
    string Key,
    RemoteKeyAction Action,
    int Repeat,
    TimeSpan DelayAfter);

public sealed record MenuAuthoringCandidateSummary(
    MenuAuthoringItemKind Kind,
    string Id,
    string Label,
    string? SourceNodeId,
    string TargetNodeId,
    string? SourcePath,
    string TargetPath,
    int CommandCount,
    int ReplayDelayMilliseconds);

public sealed record MenuAuthoringSnapshot(
    bool IsRecording,
    MenuAuthoringItemKind? RecordingKind,
    string? RecordingId,
    string? RecordingLabel,
    string? SourceNodeId,
    string? TargetNodeId,
    IReadOnlyList<MenuRecordedStepSummary> RecordedSteps,
    int RecordedCommandCount,
    IReadOnlyList<MenuAuthoringCandidateSummary> DraftCandidates,
    MenuAuthoringItemKind? ActiveValidationKind,
    string? ActiveValidationId,
    int ValidationPasses,
    int RequiredValidationPasses,
    bool AwaitingValidationConfirmation,
    string? ExpectedTargetPath,
    string? Status,
    string? Error);
