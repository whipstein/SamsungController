using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SamsungController.Web.Services;

// Private settings only. Never serialize a profile/connection, token, certificate or response payload.
public sealed record IpMenuSavedContext(string Endpoint, string Model, string Firmware, string Signal, string Input, string PictureMode)
{
    public static IpMenuSavedContext From(IpRemoteProfile profile, IpMenuSnapshot menu) =>
        new(profile.Endpoint, profile.Model, profile.Firmware, profile.Signal, menu.Input ?? "", menu.PictureMode ?? "");
}

public sealed record IpMenuSavedState
{
    public string Format { get; init; } = "SamsungController.SettingsState.v1";
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DateTimeOffset SavedAt { get; init; }
    public IpMenuSavedContext Context { get; init; } = new("", "", "", "", "", "");
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Missing { get; init; } = new(StringComparer.Ordinal);
}

public sealed record IpMenuStateRecall(Guid StateId, string Name, IpMenuSavedContext Context, DateTimeOffset StartedAt)
{
    public string Status { get; init; } = "Running";
    public int Confirmed { get; init; }
    public Dictionary<string, string> Skipped { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> FinalModes { get; init; } = new(StringComparer.Ordinal);
    public string Message { get; init; } = "";
    [JsonIgnore] public bool NeedsReview => Status is "Running" or "Stopped";
}

public static class IpMenuSavedStates
{
    // Context changes can switch whole calibration banks. They must be selected explicitly before recall.
    // Unindexed RGB/interval/color controls are replaced by the complete, fixed indexed rows.
    public static IEnumerable<IpMenuControl> Controls => IpMenuCatalog.AllControls.Where(control =>
        !control.ChangesContext && !control.IsSelector && (control.IsIndexed || !IpMenuGrids.All.Any(grid => grid.Fields.Contains(control.Field))));

    public static IpMenuSavedState Capture(string name, IpRemoteProfile profile, IpMenuSnapshot menu, DateTimeOffset now)
    {
        var state = new IpMenuSavedState { Name = name.Trim(), SavedAt = now, Context = IpMenuSavedContext.From(profile, menu) };
        foreach (var control in Controls)
        {
            var value = menu.Value(control)?.ToString();
            if (value is not null)
            {
                try { _ = IpMenuCatalog.ParseTarget(control, value); state.Values.Add(control.Id, value); continue; }
                catch (ArgumentException) { }
            }
            state.Missing.Add(control.Id, value is null ? "Not read or not reported by this display." : "Reported value is outside the documented options/range.");
        }
        Validate(state);
        return state;
    }

    public static void Validate(IpMenuSavedState state)
    {
        if (state.Format != "SamsungController.SettingsState.v1" || state.Id == Guid.Empty || state.SavedAt == default)
            throw new InvalidOperationException("This is not a valid saved settings state.");
        if (string.IsNullOrWhiteSpace(state.Name) || state.Name.Length > 80 || state.Name.Any(char.IsControl))
            throw new InvalidOperationException("State names must contain 1–80 characters without control characters.");
        if (state.Context is null || new[] { state.Context.Endpoint, state.Context.Model, state.Context.Firmware, state.Context.Signal, state.Context.Input, state.Context.PictureMode }
            .Any(value => value is null || value.Length > 512) || string.IsNullOrWhiteSpace(state.Context.Input) || string.IsNullOrWhiteSpace(state.Context.PictureMode)
            || !Uri.TryCreate(state.Context.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || endpoint.UserInfo.Length > 0)
            throw new InvalidOperationException("The saved state's display/input/picture-mode context is invalid.");
        var allowed = Controls.ToDictionary(control => control.Id, StringComparer.Ordinal);
        if (state.Values is null || state.Values.Count == 0 || state.Values.Count > allowed.Count || state.Missing is null || state.Missing.Count > allowed.Count)
            throw new InvalidOperationException("There are no usable TV values to save, or the saved values are invalid. Refresh state first.");
        foreach (var (id, value) in state.Values)
        {
            if (!allowed.TryGetValue(id, out var control) || value is null) throw new InvalidOperationException("The state contains an unsupported setting.");
            _ = IpMenuCatalog.ParseTarget(control, value);
        }
        if (state.Missing.Any(pair => !allowed.ContainsKey(pair.Key) || state.Values.ContainsKey(pair.Key) || pair.Value is null || pair.Value.Length > 512))
            throw new InvalidOperationException("The saved state's missing-value list is invalid.");
    }

    public static IEnumerable<IpMenuControl> DependencyOrder(IEnumerable<IpMenuControl> controls)
    {
        var remaining = controls.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(control => !control.Command.Requirements.Any(requirement =>
                remaining.Any(parent => parent.Method == requirement.Method && parent.Field == requirement.Field)))
                ?? throw new InvalidOperationException("The saved settings have circular prerequisites.");
            remaining.Remove(next);
            yield return next;
        }
    }
}
