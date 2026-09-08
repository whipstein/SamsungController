namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    public Task MoveExpertGroupAsync(string source, string target, bool after) => SaveExpertLayoutAsync(
        preferences => IpExpertLayout.Move(preferences.ExpertGroupOrder, source, target, after));

    public Task ResetExpertLayoutAsync() => SaveExpertLayoutAsync(_ => IpExpertLayout.Normalize(null));

    private async Task SaveExpertLayoutAsync(Func<IpMenuPreferences, IReadOnlyList<string>> arrange)
    {
        // Uses only private UI preferences: no connection, TV query, draft change,
        // setting write, or value-cache invalidation is involved.
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var preferences = GetSnapshot().Menu.Preferences;
            var order = arrange(preferences);
            if (order.SequenceEqual(preferences.ExpertGroupOrder, StringComparer.Ordinal)) return;
            preferences = preferences with { ExpertGroupOrder = order };
            await SaveMenuFileAsync(MenuPreferencesPath, preferences).ConfigureAwait(false);
            UpdateMenu(menu => menu with { Preferences = preferences });
        }
        finally { _gate.Release(); }
    }
}
