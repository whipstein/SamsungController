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
    bool AllowUntrustedCertificate = true,
    int KeepAliveIntervalSeconds = 20,
    int KeepAliveTimeoutSeconds = 10,
    int PostConnectWarmupMilliseconds = 1500,
    int ReconnectAfterIdleSeconds = 300);

public sealed record ControllerSnapshot(
    string DisplayName,
    string? Host,
    string ApplicationName,
    bool Secure,
    int? Port,
    bool AllowUntrustedCertificate,
    int KeepAliveIntervalSeconds,
    int KeepAliveTimeoutSeconds,
    int PostConnectWarmupMilliseconds,
    int ReconnectAfterIdleSeconds,
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
    MenuStateConfidence MenuConfidence,
    string? DisplayDefinitionPath = null);

public sealed record DisplayDefinitionCatalogEntry(
    string Path,
    string Id,
    string Name,
    string? Host,
    string MenuDefinitionId,
    string? MenuDefinitionName,
    string? ResolvedMenuDefinitionPath,
    string? MenuConfigurationId,
    string? DefaultMenuReferenceId,
    IReadOnlyList<DisplayMenuReferenceSummary> Menus,
    string Location,
    bool IsActive,
    bool IsValid = true,
    string? Error = null);

public sealed record DisplayMenuReferenceSummary(
    string Id,
    string MenuDefinitionId,
    string MenuDefinitionName,
    string ResolvedMenuDefinitionPath,
    string? MenuConfigurationId,
    bool IsDefault);

public sealed record DisplayDefinitionEditRequest(
    string Id,
    string Name,
    string? Host,
    bool Secure,
    int? Port,
    bool AllowUntrustedCertificate,
    string MenuDefinitionPath,
    string MenuDefinitionId,
    string? MenuConfigurationId,
    int? KeepAliveIntervalSeconds = null,
    int? KeepAliveTimeoutSeconds = null,
    int? PostConnectWarmupMilliseconds = null,
    int? ReconnectAfterIdleSeconds = null);

public sealed record DisplayDefinitionSavePreview(
    string Id,
    string Path,
    bool FileExists,
    bool IsActiveUserDefinition);

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

public sealed record MenuControlBehaviorPreferences(
    bool ApplyImmediately,
    bool ReturnToNormalVideo);

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
    int StepCount,
    bool Verified,
    int VerificationPasses,
    string? StartingNodeId,
    bool ConfirmBeforeRun);

public sealed record MacroDetails(
    string Name,
    string? Description,
    IReadOnlyList<MacroStep> Steps,
    bool Verified,
    int VerificationPasses,
    string? StartingNodeId,
    bool ConfirmBeforeRun);

public sealed record MacroEditRequest(
    string Name,
    string? Description,
    IReadOnlyList<MacroStep> Steps,
    string? StartingNodeId = null,
    bool ConfirmBeforeRun = false);

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
    bool HasDraftRoute,
    MenuControlType ControlType,
    string? DefaultValue,
    IReadOnlyList<MenuNodeDisabledCondition> DisabledWhen,
    bool Disabled,
    bool IsPermanentlyDisabled,
    bool IsDisabledByDefault,
    IReadOnlyList<MenuNodeHiddenCondition> HiddenWhen,
    bool IsHiddenByDefault,
    IReadOnlyList<string> SelectionOptions,
    decimal? MinimumValue,
    decimal? MaximumValue,
    IReadOnlyList<MenuNodeDefaultValueRule>? DefaultValueWhen = null,
    string? EffectiveDefaultValue = null)
{
    public string? ResolvedDefaultValue => EffectiveDefaultValue ?? DefaultValue;
}

public sealed record MenuSliderValueUpdate(
    string NodeId,
    decimal FromValue,
    decimal ToValue);

public sealed record MenuControlValueUpdate(
    string NodeId,
    string FromValue,
    string ToValue);

public sealed record MenuIndexedControlValueUpdate(
    string SelectorNodeId,
    string SelectorValue,
    string NodeId,
    string FromValue,
    string ToValue);

public sealed record MenuControlProfileValue(
    string NodeId,
    string Value,
    string? SelectorNodeId = null,
    string? SelectorValue = null);

public sealed record MenuControlProfileSnapshot(
    string? DefinitionId,
    IReadOnlyList<MenuControlProfileValue> Values);

public sealed record MenuControlTargetProfile(
    int Version,
    string Name,
    string DefinitionId,
    string? DefinitionName,
    string? Model,
    MenuDefinitionContext? Context,
    DateTimeOffset ExportedAtUtc,
    IReadOnlyList<MenuControlProfileValue> Values);

public sealed record SavedMenuControlState(
    string Id,
    string Name,
    string DefinitionId,
    DateTimeOffset SavedAtUtc,
    IReadOnlyList<MenuControlProfileValue> Values);

public sealed record SavedMenuControlStateSummary(
    string Id,
    string Name,
    DateTimeOffset SavedAtUtc,
    int ValueCount);

public sealed record MenuControlVerificationSnapshot(
    int ConfirmedSliderCount,
    int RequiredSliderCount,
    bool SlidersVerified,
    IReadOnlyList<string> ConfirmedSliderNodeIds,
    int ConfirmedSelectionCount,
    int RequiredSelectionCount,
    bool SelectionsVerified,
    IReadOnlyList<string> ConfirmedSelectionNodeIds,
    IReadOnlyList<MenuControlType> VerifiedSelectionControlTypes);

public sealed record MenuDefinitionVerificationCheckSummary(
    string Id,
    MenuVerificationCheckKind Kind,
    string Label,
    string Description,
    bool Verified,
    bool ExistingEvidenceReady,
    bool IsForActiveConfiguration,
    string? TargetNodeId,
    string? ConfigurationId,
    MenuAuthoringItemKind? AuthoringItemKind,
    string? AuthoringItemId,
    MenuReturnScriptKind? ReturnScriptKind,
    int ValidationPasses,
    int RequiredValidationPasses,
    bool AwaitingValidationConfirmation,
    DateTimeOffset? VerifiedAtUtc,
    string? RelatedReturnCheckId = null);

public sealed record MenuDefinitionVerificationSnapshot(
    string? DefinitionName,
    string? DefinitionPath,
    string CurrentDisplay,
    string? RecordedDisplay,
    bool FullyVerified,
    int VerifiedCount,
    int RequiredCount,
    int StaleRecordCount,
    DateTimeOffset? LastVerifiedAtUtc,
    IReadOnlyList<MenuDefinitionVerificationCheckSummary> Checks)
{
    public int RemainingCount => Math.Max(0, RequiredCount - VerifiedCount);
}

public sealed record MenuDefinitionVerificationTestResult(
    string CheckId,
    string TargetNodeId,
    string TargetPath,
    MenuControlType? ControlType,
    string ActionDescription,
    IReadOnlyList<MenuControlValueUpdate> AppliedUpdates,
    IReadOnlyList<string>? DefaultBasedNodeIds = null);

public sealed record MenuAnchorSummary(
    string Id,
    string Label,
    string TargetNodeId,
    string TargetPath,
    string? Description,
    bool Verified,
    int CommandCount);

public sealed record MenuConfigurationSummary(
    string Id,
    string Name,
    string? Conditions,
    bool IsActive);

public sealed record MenuExternalStateValue(
    string Id,
    string Value);

public sealed record MenuExternalStateSummary(
    string Id,
    string Label,
    string Value,
    string DefaultValue,
    IReadOnlyList<string> Options);

public sealed record MenuDefinitionCatalogEntry(
    string Path,
    string Id,
    string Name,
    string Model,
    MenuDefinitionContext Context,
    string Location,
    bool IsActive,
    bool IsValid = true,
    string? Error = null);

public sealed record MenuNavigationSnapshot(
    string DefinitionPath,
    string? DefinitionName,
    string? Model,
    MenuDefinitionContext? Context,
    IReadOnlyList<MenuConfigurationSummary> Configurations,
    string? ActiveConfigurationId,
    IReadOnlyList<MenuNodeSummary> Nodes,
    IReadOnlyList<MenuAnchorSummary> Anchors,
    MenuState State,
    NavigationPlan? Plan,
    bool IsRunning,
    string? Status,
    NavigationProgress? Progress,
    string? Error,
    IReadOnlyDictionary<string, string> ControlValues,
    IReadOnlyList<MenuExternalStateSummary>? ExternalStates = null);

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
    MenuDefinitionFileFormat Format = MenuDefinitionFileFormat.Yaml);

public sealed record MenuDefinitionCreationPreview(
    string Id,
    string Name,
    string Path,
    bool FileExists);

public sealed record MenuConfigurationEditRequest(
    string Id,
    string Name,
    string? Conditions);

public sealed record MenuNodeEditRequest(
    string Id,
    string Label,
    string? ParentId,
    string? Description,
    MenuControlType ControlType = MenuControlType.Submenu,
    string? DefaultValue = null,
    IReadOnlyList<MenuNodeDisabledCondition>? DisabledWhen = null,
    IReadOnlyList<string>? SelectionOptions = null,
    decimal? MinimumValue = null,
    decimal? MaximumValue = null,
    IReadOnlyList<MenuNodeHiddenCondition>? HiddenWhen = null,
    bool Disabled = false,
    IReadOnlyList<MenuNodeDefaultValueRule>? DefaultValueWhen = null);

public sealed record MenuTopologyOutlineRequest(
    string ParentNodeId,
    string Outline,
    bool KeepUnlistedNodes = false);

public sealed record MenuTopologyOutlinePreview(
    int OutlineNodeCount,
    int AddedNodeCount,
    int UpdatedNodeCount,
    int RemovedNodeCount,
    int ReorderedLevelCount,
    int FinalNodeCount,
    IReadOnlyList<string> Changes)
{
    public bool HasChanges => AddedNodeCount > 0
        || UpdatedNodeCount > 0
        || RemovedNodeCount > 0
        || ReorderedLevelCount > 0;
}

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
                && transition.ToNodeId.Equals(targetNodeId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    transition.ConfigurationId,
                    definition.ActiveConfigurationId,
                    StringComparison.OrdinalIgnoreCase))
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
    IReadOnlyList<MenuAuthoringReplayStepSummary> ReturnReplaySteps,
    bool GeneratedFromTopology = false,
    int CoveredRouteCount = 1);

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
    string? ExpectedTargetPath,
    bool HasEntryRoute = false,
    string? EntryTransitionId = null,
    string? EntryScript = null,
    bool EntryVerified = false);

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
