using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string CommandsPath => Path.Combine(_directory, "command-tests.json");
    private sealed record SavedCommandTests(IpRemoteCommandTrial? Current, IReadOnlyList<IpRemoteCommandTrial> History);

    public Task QueryCatalogAsync(string method) => RunPictureOperationAsync(async (profile, cancellation) =>
    {
        SamsungIpRemoteCommands.Get(method).Validate(new(), query: true);
        Update(state => state with { DirectPictureReading = null, WorkspaceReading = null, CatalogQuery = null });
        var exchange = await _client.ExecuteCommandAsync(profile.Connection, method, new(), query: true, cancellationToken: cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, "Command catalog · read/list", exchange).ConfigureAwait(false);
        Update(state => state with { CatalogQuery = new(profile, exchange) });
        RequireSuccess(exchange);
    });

    public Task PrepareCatalogCommandAsync(string method, JsonObject parameters)
    {
        var command = SamsungIpRemoteCommands.Get(method);
        var plan = command.Validate(parameters, query: false);
        if (command.ManagedPictureControl is not null) throw new InvalidOperationException("Use the verified picture workflow for this setting. The command tester cannot bypass picture verification/ranges.");
        return RunPictureOperationAsync(async (profile, cancellation) =>
        {
            EnsureNoPendingPictureTest();
            if (new[] { profile.Model, profile.Firmware, profile.InputSource, profile.PictureMode, profile.Signal }.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Save complete display/input/mode/signal annotations before testing commands.");
            if (command.DeviceList) ValidateDiscoveredDevice(profile, command, plan);
            ValidateListedCommandValue(profile, command, plan);
            Update(state => state with { DirectPictureReading = null, WorkspaceReading = null });
            var before = await ReadCommandStateAsync(profile, "Command · prepare (read only)", cancellation).ConfigureAwait(false);
            var prerequisites = await ReadCommandRequirementsAsync(profile, command, cancellation).ConfigureAwait(false);
            var specific = await ReadDedicatedCommandAsync(profile, command, cancellation).ConfigureAwait(false);
            if (command.HasDedicatedReadback)
            {
                var fields = CommandFields(command, specific);
                if (plan.Any(item => !UsableOriginal(command.Parameters.Single(parameter => parameter.Name == item.Key), fields?[item.Key])))
                    throw new InvalidOperationException("The dedicated getter did not return the requested setting fields. Inspect Read/list and the diagnostic report; no command was sent and missing values were not replaced with defaults.");
                if (plan.All(item => EquivalentCommandValue(fields![item.Key], item.Value)))
                    throw new InvalidOperationException("The TV already reports this target. Choose a different valid value to test a real change; no command was sent.");
            }
            var originalState = command.ReadbackMethod == "getVideoStates" ? before.Video : before.Tv;
            if (command.ReadbackField is { } originalField && originalState[originalField] is { } originalValue
                && EquivalentCommandValue(originalValue, plan[originalField]))
                throw new InvalidOperationException("The TV already reports this target. Choose a different valid value to test a real change; no command was sent.");
            await SaveCommandTestsAsync(new IpRemoteCommandTrial
            {
                Profile = profile,
                Method = method,
                Parameters = plan,
                PreparedAt = _timeProvider.GetUtcNow(),
                BeforeTv = before.Tv,
                BeforeVideo = before.Video,
                BeforeCommand = specific,
                Prerequisites = prerequisites,
                Stage = IpRemoteCommandStage.Prepared
            }).ConfigureAwait(false);
        });
    }

    public Task ExecutePreparedCatalogCommandAsync(Guid id, bool confirmed) => RunPictureOperationAsync(async (profile, cancellation) =>
    {
        EnsureNoPendingPictureTest();
        var trial = GetSnapshot().CommandTrial;
        if (trial is null || trial.Id != id || trial.Profile != profile || trial.Stage != IpRemoteCommandStage.Prepared
            || trial.WriteAttempted || _timeProvider.GetUtcNow() - trial.PreparedAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Prepare this command again. The baseline is missing, stale, or from another display.");
        if (!confirmed) throw new InvalidOperationException("Confirm the exact command, valid values, and original display conditions before sending.");
        var command = SamsungIpRemoteCommands.Get(trial.Method);
        var plan = command.Validate(trial.Parameters, query: false);
        if (command.ManagedPictureControl is not null) throw new InvalidOperationException("Use verified picture controls for this setting.");
        if (command.DeviceList) ValidateDiscoveredDevice(profile, command, plan);
        ValidateListedCommandValue(profile, command, plan);
        var before = await ReadCommandStateAsync(profile, "Command · preflight", cancellation).ConfigureAwait(false);
        var prerequisites = await ReadCommandRequirementsAsync(profile, command, cancellation).ConfigureAwait(false);
        var specific = await ReadDedicatedCommandAsync(profile, command, cancellation).ConfigureAwait(false);
        if (!JsonNode.DeepEquals(before.Tv, trial.BeforeTv) || !JsonNode.DeepEquals(before.Video, trial.BeforeVideo)
            || !JsonNode.DeepEquals(specific, trial.BeforeCommand) || !JsonNode.DeepEquals(prerequisites, trial.Prerequisites))
            throw new InvalidOperationException("The reported TV state changed after preparation. Prepare again; no command was sent.");
        Update(state => state with { DirectPictureReading = null, WorkspaceReading = null });
        trial = trial with { WriteAttempted = true, Stage = IpRemoteCommandStage.Sending, Message = "Sending one command. Stop cannot undo a delivered action." };
        // If this initial save fails, nothing can have been sent.
        await SaveCommandTestsAsync(trial).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var exchange = await _client.ExecuteCommandAsync(profile.Connection, command.Method, plan, cancellationToken: cancellation).ConfigureAwait(false);
            await RecordExchangeAsync(profile, "Command · explicit send", exchange).ConfigureAwait(false);
            trial = trial with { Acknowledged = exchange.IsSuccess };
            RequireSuccess(exchange);
            cancellation.ThrowIfCancellationRequested();
            if (!command.SkipReadback)
            {
                var after = await ReadCommandStateAsync(profile, "Command · independent readback", cancellation).ConfigureAwait(false);
                var state = command.ReadbackMethod == "getVideoStates" ? after.Video : after.Tv;
                bool? matches = command.ReadbackField is { } field && state[field] is { } actual
                    ? EquivalentCommandValue(actual, plan[field]) : null;
                var originalState = command.ReadbackMethod == "getVideoStates" ? trial.BeforeVideo : trial.BeforeTv;
                var changed = matches == true && command.ReadbackField is { } changedField && originalState[changedField] is { } original
                    && !EquivalentCommandValue(original, plan[changedField]);
                if (command.Method is "brightnessControl" or "tintControl")
                {
                    var expectedOthers = (JsonObject)trial.BeforeVideo.DeepClone();
                    var actualOthers = (JsonObject)after.Video.DeepClone();
                    expectedOthers.Remove(command.ReadbackField!); actualOthers.Remove(command.ReadbackField!);
                    if (!JsonNode.DeepEquals(trial.BeforeTv, after.Tv) || !JsonNode.DeepEquals(expectedOthers, actualOthers))
                    { matches = false; changed = false; }
                }
                if (command.HasDedicatedReadback)
                {
                    var afterSpecific = await ReadDedicatedCommandAsync(profile, command, cancellation).ConfigureAwait(false);
                    var afterPrerequisites = await ReadCommandRequirementsAsync(profile, command, cancellation).ConfigureAwait(false);
                    var fields = CommandFields(command, afterSpecific);
                    var originals = CommandFields(command, trial.BeforeCommand);
                    matches = plan.All(item => fields?[item.Key] is JsonValue && EquivalentCommandValue(fields[item.Key], item.Value));
                    changed = matches == true && plan.Any(item => originals?[item.Key] is JsonValue && !EquivalentCommandValue(originals[item.Key], item.Value));
                    // A channel write must not silently target another interval/color or alter other WB2 channels.
                    var mayChangeMode = command.Method is "artModeControl" or "gameModeControl" or "pictureCalibrationModeControl";
                    if (!JsonNode.DeepEquals(afterPrerequisites, trial.Prerequisites)
                        || (!mayChangeMode && (!EquivalentCommandValue(after.Tv["inputSource"], trial.BeforeTv["inputSource"])
                            || !EquivalentCommandValue(after.Tv["pictureMode"], trial.BeforeTv["pictureMode"]))))
                    { matches = false; changed = false; }
                    if (command.Parameters.All(parameter => parameter.Kind == IpRemoteParameterKind.Integer)
                        && (!JsonNode.DeepEquals(trial.BeforeTv, after.Tv) || !JsonNode.DeepEquals(trial.BeforeVideo, after.Video)))
                    { matches = false; changed = false; }
                    if (command.Method == "WB2PointControl" && originals is not null && fields is not null
                        && originals.Any(item => !plan.ContainsKey(item.Key) && !EquivalentCommandValue(fields[item.Key], item.Value)))
                    { matches = false; changed = false; }
                    trial = trial with { AfterCommand = afterSpecific, AfterPrerequisites = afterPrerequisites };
                }
                trial = trial with { AfterTv = after.Tv, AfterVideo = after.Video, ReadbackMatches = matches, ChangeObserved = changed };
            }
            trial = trial with
            {
                Stage = IpRemoteCommandStage.AwaitingReview,
                Message = trial.ReadbackMatches switch
                {
                    true => "Independent readback matches the requested field. Inspect the TV before confirming. Other settings may have changed with this command.",
                    false => "Readback does NOT match the target, or another field/context/interval/color changed during the test. Do not count this as verified. Check/restore the TV manually; nothing was retried.",
                    null => "Command acknowledged, but no independent target confirmation is available. Inspect the actual effect; this cannot establish read/write verification."
                }
            };
            await SaveCommandTestsAsync(trial).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            trial = trial with { Stage = IpRemoteCommandStage.Uncertain, Message = $"Stopped: {error.Message} Check the TV and handle any change manually. No retry, automatic restoration, or resume was sent." };
            Update(state => state with { CommandTrial = trial, Status = trial.Message });
            try { await SaveCommandTestsAsync(trial).ConfigureAwait(false); }
            catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
            { Update(state => state with { StorageWarning = "Could not save command outcome. Keep the baseline shown here and check the TV before closing." }); }
            throw;
        }
    });

    public async Task ConfirmCatalogCommandAsync(Guid id, bool observedExpectedEffect)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var trial = GetSnapshot().CommandTrial;
            if (trial is null || trial.Id != id || !trial.RequiresReview || trial.Profile != GetSnapshot().ActiveProfile)
                throw new InvalidOperationException("No matching command is awaiting review.");
            if (observedExpectedEffect && (!trial.Acknowledged || trial.ReadbackMatches == false))
                throw new InvalidOperationException("A rejected/unconfirmed command or mismatched readback cannot be counted as successful. Check/handle the TV manually and close as unsuccessful.");
            trial = trial with
            {
                Stage = observedExpectedEffect ? IpRemoteCommandStage.Confirmed : IpRemoteCommandStage.Failed,
                VisualConfirmed = observedExpectedEffect,
                Message = observedExpectedEffect ? "Expected effect confirmed by user. The resulting state is kept; no restoration was sent."
                    : "User checked/handled the TV manually. Closed without verification or any command."
            };
            var history = GetSnapshot().CommandHistory.Where(item => item.Id != trial.Id).Append(trial).TakeLast(100).ToArray();
            await SaveCommandTestsAsync(trial, history).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static bool EquivalentCommandValue(JsonNode? actual, JsonNode? expected) => JsonNode.DeepEquals(actual, expected)
        || (actual is JsonValue a && expected is JsonValue b && a.TryGetValue<string>(out var left) && b.TryGetValue<string>(out var right)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        || (actual is JsonValue textValue && expected is JsonValue intValue && intValue.TryGetValue<int>(out var target)
            && textValue.TryGetValue<string>(out var numericText)
            && int.TryParse(numericText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number) && number == target);

    internal static JsonObject? CommandFields(SamsungIpRemoteCommand command, JsonObject? result)
    {
        if (command.Method != "WB2PointControl" || result is null || !result.ContainsKey("WB2Point")) return result;
        if (result["WB2Point"] is JsonObject channels) return channels;
        if (result["WB2Point"] is JsonValue value && value.TryGetValue<string>(out var json))
        {
            try { return JsonNode.Parse(json) as JsonObject; }
            catch (JsonException) { return null; }
        }
        return null;
    }

    private static bool UsableOriginal(IpRemoteParameter parameter, JsonNode? node)
    {
        if (node is not JsonValue value) return false;
        if (parameter.Kind != IpRemoteParameterKind.Integer)
            return value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= 256;
        var hasNumber = value.TryGetValue<int>(out var number)
            || (value.TryGetValue<string>(out var numericText)
                && int.TryParse(numericText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out number));
        return hasNumber && number >= parameter.Minimum && number <= parameter.Maximum;
    }

    private async Task<JsonObject?> ReadDedicatedCommandAsync(IpRemoteProfile profile, SamsungIpRemoteCommand command, CancellationToken cancellation)
    {
        if (!command.HasDedicatedReadback) return null;
        return await ReadCatalogObjectAsync(profile, command.ReadbackMethod!, "Command · dedicated setting read (no value sent)", cancellation).ConfigureAwait(false);
    }

    private async Task<JsonObject> ReadCommandRequirementsAsync(IpRemoteProfile profile, SamsungIpRemoteCommand command, CancellationToken cancellation)
    {
        var states = new JsonObject();
        foreach (var requirement in command.Requirements)
        {
            var state = await ReadCatalogObjectAsync(profile, requirement.Method, "Command · mode/selector prerequisite (read only)", cancellation).ConfigureAwait(false);
            if (state[requirement.Field] is not JsonValue value || !value.TryGetValue<string>(out var text)
                || !requirement.AllowedValues.Contains(text, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{command.Name} requires {requirement.Field}: {string.Join(" / ", requirement.AllowedValues)}. Set it explicitly, then prepare again. No mode, interval or color was selected automatically.");
            states[requirement.Method] = state;
        }
        return states;
    }

    private async Task<JsonObject> ReadCatalogObjectAsync(IpRemoteProfile profile, string method, string label, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var exchange = await _client.ExecuteCommandAsync(profile.Connection, method, new(), query: true, cancellationToken: cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, label, exchange).ConfigureAwait(false);
        RequireSuccess(exchange);
        cancellation.ThrowIfCancellationRequested();
        return exchange.Result is { } result ? (JsonObject)result.DeepClone()
            : throw new InvalidOperationException($"{method} did not return setting fields. Inspect its Read/list result before writing; no defaults were assumed.");
    }

    private void ValidateDiscoveredDevice(IpRemoteProfile profile, SamsungIpRemoteCommand command, JsonObject plan)
    {
        var query = GetSnapshot().CatalogQuery;
        if (query is null || query.Profile != profile || query.Exchange.Method != command.Method || !query.Exchange.IsSuccess
            || _timeProvider.GetUtcNow() - query.Exchange.Timestamp > TimeSpan.FromMinutes(2)
            || query.Exchange.Payload is not JsonArray devices || !devices.OfType<JsonObject>().Any(device =>
                JsonNode.DeepEquals(device["deviceId"], plan["deviceId"]) && JsonNode.DeepEquals(device["deviceName"], plan["deviceName"])))
            throw new InvalidOperationException("Read/list devices first, then choose an actual returned device. Missing or invented device IDs are not accepted.");
    }

    private void ValidateListedCommandValue(IpRemoteProfile profile, SamsungIpRemoteCommand command, JsonObject plan)
    {
        if (command.Method is not ("firstScreenAppControl" or "multiviewControl")) return;
        var query = GetSnapshot().CatalogQuery;
        var target = plan[command.Parameters[0].Name]!.GetValue<string>();
        static bool Contains(JsonNode? node, string expected) => node switch
        {
            JsonObject obj => obj.Any(item => Contains(item.Value, expected)),
            JsonArray array => array.Any(item => Contains(item, expected)),
            JsonValue value => value.TryGetValue<string>(out var text) && string.Equals(text, expected, StringComparison.Ordinal),
            _ => false
        };
        if (query is null || query.Profile != profile || query.Exchange.Method != command.Method || !query.Exchange.IsSuccess
            || _timeProvider.GetUtcNow() - query.Exchange.Timestamp > TimeSpan.FromMinutes(2)
            || !Contains(query.Exchange.Payload ?? query.Exchange.Result, target))
            throw new InvalidOperationException("Read/list this command first and use an exact value returned by this display within two minutes. An empty list or guessed value cannot be sent.");
    }

    private async Task<(JsonObject Tv, JsonObject Video)> ReadCommandStateAsync(IpRemoteProfile profile, string label, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var tv = await _client.ReadAsync(profile.Connection, "getTVStates", cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, label, tv).ConfigureAwait(false);
        RequireSuccess(tv);
        _ = RequiredText(tv.Result, "inputSource"); _ = RequiredText(tv.Result, "pictureMode");
        cancellation.ThrowIfCancellationRequested();
        var video = await _client.ReadAsync(profile.Connection, "getVideoStates", cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, label, video).ConfigureAwait(false);
        RequireSuccess(video);
        cancellation.ThrowIfCancellationRequested();
        if (video.Result is null) throw new InvalidOperationException("No video baseline was returned.");
        return ((JsonObject)tv.Result!.DeepClone(), (JsonObject)video.Result.DeepClone());
    }

    private async Task SaveCommandTestsAsync(IpRemoteCommandTrial trial, IReadOnlyList<IpRemoteCommandTrial>? history = null)
    {
        history ??= GetSnapshot().CommandHistory;
        var temporary = CommandsPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new SavedCommandTests(trial, history), JsonOptions)).ConfigureAwait(false);
        RestrictFile(temporary);
        File.Move(temporary, CommandsPath, overwrite: true);
        Update(state => state with { CommandTrial = trial, CommandHistory = history, Status = trial.Message });
    }

    private async Task<SavedCommandTests> LoadCommandTestsAsync()
    {
        if (!File.Exists(CommandsPath)) return new(null, []);
        var saved = JsonSerializer.Deserialize<SavedCommandTests>(await File.ReadAllTextAsync(CommandsPath).ConfigureAwait(false))
            ?? throw new JsonException("Command test file is empty.");
        if (saved.History is null || saved.History.Count > 100) throw new JsonException("Invalid command history.");
        foreach (var trial in saved.History.Concat(saved.Current is null ? [] : new[] { saved.Current }))
        {
            if (trial is null || trial.Id == Guid.Empty || trial.Profile?.Connection is null || trial.Parameters is null || trial.BeforeTv is null || trial.BeforeVideo is null || trial.Prerequisites is null
                || !Enum.IsDefined(trial.Stage)) throw new JsonException("Invalid command test. Check the TV before discarding recovery data.");
            var definition = SamsungIpRemoteCommands.Get(trial.Method);
            definition.Validate(trial.Parameters, false);
            if (definition.ManagedPictureControl is not null || (trial.Stage == IpRemoteCommandStage.Confirmed && (!trial.WriteAttempted || !trial.Acknowledged || trial.VisualConfirmed != true || trial.ReadbackMatches == false)))
                throw new JsonException("Invalid command verification record.");
            _ = trial.Profile.Endpoint;
        }
        var current = saved.Current;
        if (current?.RequiresReview == true) current = current with { Stage = IpRemoteCommandStage.Uncertain, Message = "An earlier command needs review. Nothing was sent on startup. Check the TV and handle any changes before closing this review." };
        else if (current?.Stage == IpRemoteCommandStage.Prepared) current = current with { Stage = IpRemoteCommandStage.Expired, Message = "Previous baseline expired. Prepare again; no command was sent on startup." };
        return saved with { Current = current };
    }
}
