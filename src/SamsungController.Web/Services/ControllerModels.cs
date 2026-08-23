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
    bool AllowUntrustedCertificate,
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
    string? MenuLabel,
    MenuStateConfidence MenuConfidence);

public enum QuickAccessActionKind
{
    RemoteKey,
    Macro,
    MenuAnchor
}

public sealed record QuickAccessAction(
    string Id,
    string Label,
    QuickAccessActionKind Kind,
    string Target,
    RemoteKeyAction Action = RemoteKeyAction.Click);

public sealed record DeviceInfoObservation(
    DateTimeOffset Timestamp,
    string Label,
    string Endpoint,
    int? StatusCode,
    bool IsSuccess,
    string RawContent,
    string? ParseError,
    string? Error);

public sealed record DeviceInfoSnapshot(
    bool IsQuerying,
    IReadOnlyList<DeviceInfoObservation> Observations);

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
    string? ParentId,
    bool HasVerifiedRoute,
    bool HasDraftRoute);

public sealed record MenuAnchorSummary(
    string Id,
    string Label,
    string TargetNodeId,
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

public sealed record MenuTraversalFailureReport(
    string DraftTransitionId,
    string SourcePath,
    string TargetPath,
    string KnownStateAnchorLabel,
    string KnownStatePath);

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

public sealed record MenuNodeEditRequest(
    string Id,
    string Label,
    string? ParentId,
    string? Description);

public sealed record MenuRecordingRequest(
    MenuAuthoringItemKind Kind,
    string ItemId,
    string Label,
    string? SourceNodeId,
    string TargetNodeId,
    string? NewTargetLabel,
    string? NewTargetParentId,
    bool RecordReturnToVideo = false)
{
    public MenuRecordingRequest ResolveIdentity(MenuDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (Kind != MenuAuthoringItemKind.Transition)
        {
            return this;
        }

        if (string.IsNullOrWhiteSpace(SourceNodeId))
        {
            throw new InvalidOperationException("Choose a source control for the traversal recording.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(TargetNodeId);
        var sourceNodeId = SourceNodeId.Trim();
        var targetNodeId = TargetNodeId.Trim();
        var matchingRoutes = definition.Transitions.Values
            .Where(transition =>
                transition.FromNodeId.Equals(sourceNodeId, StringComparison.OrdinalIgnoreCase)
                && transition.ToNodeId.Equals(targetNodeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var matchingDrafts = matchingRoutes
            .Where(transition => !transition.Verified)
            .ToArray();
        if (matchingDrafts.Length > 1)
        {
            throw new InvalidOperationException(
                $"More than one draft traversal already connects '{sourceNodeId}' to '{targetNodeId}'. Remove the duplicate YAML routes before recording this target again.");
        }

        if (matchingDrafts.Length == 0 && matchingRoutes.Any(transition => transition.Verified))
        {
            throw new InvalidOperationException(
                $"The traversal from '{sourceNodeId}' to '{targetNodeId}' is already verified.");
        }

        var targetLabel = definition.Nodes.TryGetValue(targetNodeId, out var targetNode)
            ? targetNode.Label
            : string.IsNullOrWhiteSpace(NewTargetLabel)
                ? targetNodeId
                : NewTargetLabel.Trim();
        return this with
        {
            ItemId = matchingDrafts.Length == 1
                ? matchingDrafts[0].Id
                : CreateTransitionId(definition, sourceNodeId, targetNodeId),
            Label = targetLabel,
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId
        };
    }

    private static string CreateTransitionId(
        MenuDefinition definition,
        string sourceNodeId,
        string targetNodeId)
    {
        var targetBasedId = $"to-{targetNodeId}";
        if (IsAvailable(definition, targetBasedId))
        {
            return targetBasedId;
        }

        var routeBasedId = $"{sourceNodeId}-to-{targetNodeId}";
        if (IsAvailable(definition, routeBasedId))
        {
            return routeBasedId;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{routeBasedId}-{suffix}";
            if (IsAvailable(definition, candidate))
            {
                return candidate;
            }
        }
    }

    private static bool IsAvailable(MenuDefinition definition, string itemId) =>
        !definition.Transitions.ContainsKey(itemId)
        && !definition.Anchors.ContainsKey(itemId);
}

public sealed record MenuRecordedStepSummary(
    string Key,
    RemoteKeyAction Action,
    int Repeat,
    TimeSpan DelayAfter);

public sealed record MenuAuthoringReplayStepSummary(
    int Position,
    string Key,
    RemoteKeyAction Action,
    int EffectiveDelayMilliseconds,
    int SystemDelayMilliseconds,
    bool HasCustomDelay);

public sealed record MenuAuthoringReplayStepUpdate(
    int Position,
    bool UseCustomDelay,
    int DelayAfterMilliseconds);

public sealed record MenuAuthoringCandidateSummary(
    MenuAuthoringItemKind Kind,
    string Id,
    string Label,
    string? SourceNodeId,
    string TargetNodeId,
    string? SourcePath,
    string TargetPath,
    int ValidationPasses,
    int CommandCount,
    IReadOnlyList<MenuAuthoringReplayStepSummary> ReplaySteps,
    int ReturnCommandCount,
    IReadOnlyList<MenuAuthoringReplayStepSummary> ReturnReplaySteps);

public sealed record MenuTimingTestRouteSummary(
    string Id,
    string SourcePath,
    string TargetPath,
    int CommandCount,
    bool HasCustomDelays);

public enum MenuReturnScriptKind
{
    AtMenuRoot,
    BelowMenuRoot,
    NodeOverride
}

public sealed record MenuReturnScriptSummary(
    MenuReturnScriptKind Kind,
    string Label,
    string Script,
    bool Verified);

public sealed record MenuReturnTestNodeSummary(
    string Id,
    string Path);

public sealed record MenuReturnOverrideSummary(
    string NodeId,
    string Path,
    string Script,
    bool Verified);

public sealed record MenuReturnStrategySummary(
    string AnchorId,
    string AnchorLabel,
    string TargetNodeId,
    string TargetPath,
    string MenuRootNodeId,
    string MenuRootPath,
    string FallbackScript,
    MenuReturnScriptSummary AtMenuRoot,
    MenuReturnScriptSummary BelowMenuRoot,
    IReadOnlyList<MenuReturnOverrideSummary> NodeOverrides,
    IReadOnlyList<MenuReturnTestNodeSummary> DeepTestNodes,
    MenuReturnScriptKind? ActiveValidationKind,
    string? ActiveValidationNodeId,
    int ValidationPasses,
    int RequiredValidationPasses,
    bool AwaitingValidationConfirmation,
    string? ExpectedTargetPath);

public sealed record MenuAuthoringSnapshot(
    bool IsRecording,
    MenuAuthoringItemKind? RecordingKind,
    string? RecordingId,
    string? RecordingLabel,
    string? SourceNodeId,
    string? TargetNodeId,
    bool RecordsReturnToVideo,
    bool IsRecordingReturnToVideo,
    int ForwardRecordedCommandCount,
    int ReturnRecordedCommandCount,
    IReadOnlyList<MenuRecordedStepSummary> RecordedSteps,
    int RecordedCommandCount,
    MenuTimingProfile Timing,
    MenuReturnStrategySummary? ReturnStrategy,
    IReadOnlyList<MenuTimingTestRouteSummary> TimingTestRoutes,
    string? ActiveTimingTestRouteId,
    int TimingValidationPasses,
    int RequiredTimingValidationPasses,
    bool AwaitingTimingValidationConfirmation,
    string? TimingValidationExpectedTargetPath,
    IReadOnlyList<MenuAuthoringCandidateSummary> DraftCandidates,
    MenuAuthoringItemKind? ActiveValidationKind,
    string? ActiveValidationId,
    int ValidationPasses,
    int RequiredValidationPasses,
    bool AwaitingValidationConfirmation,
    string? ExpectedTargetPath,
    string? Status,
    string? Error);
