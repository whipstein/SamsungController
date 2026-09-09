using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed record IpCertificateReview(Guid Id, string Endpoint, string CertificateSha256,
    string? PreviousCertificateSha256, DateTimeOffset ObservedAt)
{
    public bool ReplacesCertificate => PreviousCertificateSha256 is not null
        && !string.Equals(PreviousCertificateSha256, CertificateSha256, StringComparison.Ordinal);
}

public sealed partial class SamsungIpRemoteService
{
    private IpRemoteProfile? _certificateReviewProfile;

    public async Task InspectDisplayCertificateAsync()
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            EnsureNoPendingPictureTest();
            var profile = GetSnapshot().ActiveProfile ?? throw new InvalidOperationException("Save a display first.");
            var cancellation = new CancellationTokenSource();
            lock (_sync) _operation = cancellation;
            _certificateReviewProfile = null;
            Update(state => state with { IsBusy = true, CertificateReview = null, Status = "Checking the display certificate only; no TV commands or token are sent…" });
            var result = await _client.InspectCertificateAsync(profile.Connection, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
            var pin = new SamsungIpRemoteOptions { Host = profile.Connection.Host, Port = profile.Connection.Port, CertificateSha256 = result.CertificateSha256 };
            if (pin.Endpoint.AbsoluteUri != result.Endpoint) throw new InvalidOperationException("The certificate check returned a different endpoint. Nothing was trusted.");
            lock (_sync)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                _certificateReviewProfile = profile;
                _snapshot = _snapshot with
                {
                    CertificateReview = new(Guid.NewGuid(), profile.Endpoint, pin.NormalizedCertificatePin!, profile.Connection.NormalizedCertificatePin, _timeProvider.GetUtcNow()),
                    Status = result.Message
                };
            }
            Changed?.Invoke();
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            Update(state => state with { CertificateReview = null, Status = "Certificate check did not complete. No trust or pairing was saved. " + error.Message });
            throw;
        }
        finally
        {
            lock (_sync) { _operation?.Dispose(); _operation = null; }
            Update(state => state with { IsBusy = false });
            _gate.Release();
        }
    }

    public Task TrustDisplayAndPairAsync(Guid reviewId, bool confirmReplacement = false)
        => RunAsync(true, [], "Trusted display pairing", reviewId, confirmReplacement);

    // Called under the operation gate. Persist strict trust before any credentials
    // or pairing requests can be sent. Never accept an arbitrary UI-supplied pin.
    private async Task<IpRemoteProfile> ConfirmCertificateLockedAsync(Guid reviewId, bool confirmReplacement)
    {
        EnsureNoPendingPictureTest();
        var state = GetSnapshot();
        var review = state.CertificateReview;
        if (review is null || review.Id != reviewId || !ReferenceEquals(state.ActiveProfile, _certificateReviewProfile)
            || _timeProvider.GetUtcNow() - review.ObservedAt > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("This certificate review expired or the display changed. Check the certificate again; nothing was trusted.");
        if (review.ReplacesCertificate && !confirmReplacement)
            throw new InvalidOperationException("The certificate differs from the saved fingerprint. Explicitly confirm replacement only after checking the display and network.");
        var profile = state.ActiveProfile! with { Connection = state.ActiveProfile!.Connection with
            { CertificateSha256 = review.CertificateSha256, AllowUntrustedCertificate = false } };
        _ = profile.Endpoint;
        var profiles = state.Profiles.Select(item => item.Endpoint == profile.Endpoint ? profile : item).ToArray();
        await SaveProfilesAsync(profiles, profile.Endpoint).ConfigureAwait(false);
        _client.CloseConnection();
        _certificateReviewProfile = null;
        Update(current => current with { ActiveProfile = profile, Profiles = profiles, CertificateReview = null,
            Menu = ResetMenu(current.Menu), DirectPictureReading = null, WorkspaceReading = null,
            Status = "Display certificate pinned. Allow untrusted is Off. Saved pairing credentials were retained." });
        return profile;
    }
}
