namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    public Task MoveExpertGroupAsync(string source, string target, bool after) => MoveMenuGroupAsync("expert", source, target, after);

    public Task ResetExpertLayoutAsync() => ResetMenuLayoutAsync("expert");

    public Task MoveMenuGroupAsync(string section, string source, string target, bool after) => SaveMenuLayoutAsync(section,
        preferences => IpExpertLayout.Move(preferences.GroupOrder(section), source, target, after, section));

    public Task ResetMenuLayoutAsync(string section) => SaveMenuLayoutAsync(section, _ => IpExpertLayout.Normalize(null, section));

    private async Task SaveMenuLayoutAsync(string section, Func<IpMenuPreferences, IReadOnlyList<string>> arrange)
    {
        // Uses only private UI preferences: no connection, TV query, draft change,
        // setting write, or value-cache invalidation is involved.
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var preferences = GetSnapshot().Menu.Preferences;
            var order = arrange(preferences);
            if (order.SequenceEqual(preferences.GroupOrder(section), StringComparer.Ordinal)) return;
            preferences = preferences.WithGroupOrder(section, order);
            await SaveMenuFileAsync(MenuPreferencesPath, preferences).ConfigureAwait(false);
            UpdateMenu(menu => menu with { Preferences = preferences });
        }
        finally { _gate.Release(); }
    }
}
