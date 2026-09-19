using OpenFno.Broker.Application.Security;
using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Security;
using OpenFno.Broker.Domain.Time;

namespace OpenFno.Broker.Application.Engine;

public sealed partial class BrokerEngine
{
    public const string TotpIssuer = "OpenFNO Broker";
    private const decimal MaxTransfer = 10_000_000_000m;

    public Task<Result<OpenedAccount>> OpenAccountAsync(string name, string profileId, CancellationToken cancellationToken = default)
        => RunAsync<OpenedAccount>(_ =>
        {
            var trimmed = name?.Trim() ?? string.Empty;
            if (trimmed.Length is 0 or > 100)
                return BrokerError.Invalid(ErrorCodes.InvalidRequest, "An account needs a name of 1 to 100 characters.");

            var profile = BrokerProfiles.Find(profileId);
            if (profile is null)
                return BrokerError.Invalid(ErrorCodes.UnknownProfile,
                    $"No broker profile '{profileId}'. Known profiles: {string.Join(", ", BrokerProfiles.All.Keys)}.");

            var secret = Totp.NewSecret();
            var encoded = Base32.Encode(secret);
            var clientId = _state.NextClientId();
            var opened = new AccountOpened
            {
                ClientId = clientId,
                Name = trimmed,
                ProfileId = profile.Id,
                ProtectedTotpSecret = _protector.Protect(encoded),
            };
            return Outcome<OpenedAccount>.Of(opened,
                _ => new OpenedAccount(clientId, encoded, Totp.ProvisioningUri(secret, clientId, TotpIssuer)));
        }, null, cancellationToken);

    public Task<Result<FundsView>> AddFundsAsync(string clientId, decimal amount, string? reference, CancellationToken cancellationToken = default)
        => RunAsync<FundsView>(_ =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            if (CheckAmount(amount) is { } invalid) return invalid;

            return Outcome<FundsView>.Of(
                new FundsAdded { ClientId = clientId, Amount = amount, Reference = CleanReference(reference, "pay-in") },
                _ => FundsOf(account));
        }, null, cancellationToken);

    public Task<Result<FundsView>> WithdrawFundsAsync(string clientId, decimal amount, string? reference, CancellationToken cancellationToken = default)
        => RunAsync<FundsView>(_ =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            if (CheckAmount(amount) is { } invalid) return invalid;
            var free = Available(account);
            if (amount > free)
                return BrokerError.Conflict(ErrorCodes.InsufficientFunds,
                    $"Only {OrderRules.Money(Math.Max(0m, free))} is free to withdraw; the rest is margin for orders and positions.");

            return Outcome<FundsView>.Of(
                new FundsWithdrawn { ClientId = clientId, Amount = amount, Reference = CleanReference(reference, "pay-out") },
                _ => FundsOf(account));
        }, null, cancellationToken);

    /// <summary>Registers an API app for the account. The secret in the answer is shown only this once.</summary>
    public Task<Result<RegisteredApp>> RegisterAppAsync(string clientId, IReadOnlyList<string>? staticIps, CancellationToken cancellationToken = default)
        => RunAsync<RegisteredApp>(_ =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            var ips = NormalizeIps(staticIps ?? [], account.Profile, allowNone: true);
            if (!ips.IsSuccess) return ips.Error!;

            var appId = Secrets.NewId("APP-");
            var secret = Secrets.NewToken();
            return Outcome<RegisteredApp>.Of(
                new AppRegistered { ClientId = clientId, AppId = appId, SecretHash = Secrets.Hash(secret), StaticIps = ips.Value },
                _ => new RegisteredApp(clientId, appId, secret, ips.Value));
        }, null, cancellationToken);

    /// <summary>Replaces an app's whitelisted IPs, at most as often as the profile allows per calendar week.</summary>
    public Task<Result<AppView>> ChangeStaticIpsAsync(string clientId, string appId, IReadOnlyList<string>? staticIps, CancellationToken cancellationToken = default)
        => RunAsync<AppView>(now =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            if (!account.Apps.TryGetValue(appId, out var app))
                return BrokerError.NotFound(ErrorCodes.AppNotFound, $"No app {appId} on account {clientId}.");

            var ips = NormalizeIps(staticIps ?? [], account.Profile, allowNone: false);
            if (!ips.IsSuccess) return ips.Error!;

            var weekStart = Ist.WeekStart(now);
            var changesThisWeek = app.StaticIpChanges.Count(at => Ist.DateOf(at) >= weekStart);
            if (changesThisWeek >= account.Profile.StaticIpChangesPerWeek)
                return BrokerError.Conflict(ErrorCodes.StaticIpChangeLimit,
                    $"Static IPs can change {account.Profile.StaticIpChangesPerWeek} time(s) per calendar week; " +
                    $"the next change is allowed from {weekStart.AddDays(7):yyyy-MM-dd} (Monday).");

            return Outcome<AppView>.Of(
                new StaticIpsChanged { ClientId = clientId, AppId = appId, StaticIps = ips.Value },
                _ => AppViewOf(app, now));
        }, null, cancellationToken);

    /// <summary>
    /// The daily login: app credentials plus a fresh TOTP code. A new login ends
    /// the app's previous session. Failed attempts are not journaled (anyone can
    /// send them); the request log keeps them.
    /// </summary>
    public Task<Result<SessionGrant>> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default)
        => RunAsync<SessionGrant>(now =>
        {
            var badCredentials = BrokerError.Unauthorized(ErrorCodes.InvalidCredentials,
                "The app ID, app secret and client ID do not match a registered app.");
            if (!_state.Apps.TryGetValue(command.AppId ?? string.Empty, out var app)
                || app.ClientId != command.ClientId
                || !Secrets.HashMatches(command.AppSecret ?? string.Empty, app.SecretHash))
                return badCredentials;

            var account = _state.Account(app.ClientId);
            var secret = Base32.Decode(_protector.Unprotect(account.ProtectedTotpSecret));
            if (Totp.Verify(secret, command.Totp, now, account.LastTotpStep) is not { } step)
                return BrokerError.Unauthorized(ErrorCodes.InvalidTotp, "The TOTP code is wrong, expired or already used.");

            var token = Secrets.NewToken();
            var tokenHash = Secrets.Hash(token);
            var expiresAt = Ist.Next(now, account.Profile.SessionExpiresAt);

            var events = new List<BrokerEvent>();
            if (app.ActiveTokenHash is { } previous)
                events.Add(new SessionClosed { ClientId = app.ClientId, TokenHash = previous, Reason = "Replaced by a new login." });
            events.Add(new SessionOpened
            {
                ClientId = app.ClientId,
                AppId = app.AppId,
                TokenHash = tokenHash,
                ExpiresAt = expiresAt,
                TotpStep = step,
                ClientIp = command.ClientIp,
            });

            return Outcome<SessionGrant>.Of(events, _ => new SessionGrant(token, app.ClientId, app.AppId, expiresAt));
        }, null, cancellationToken);

    public Task<Result<bool>> LogoutAsync(Principal principal, CancellationToken cancellationToken = default)
        => RunAsync<bool>(_ =>
        {
            if (!_state.Sessions.ContainsKey(principal.TokenHash)) return Outcome<bool>.Nothing(false);
            return Outcome<bool>.Of(
                new SessionClosed { ClientId = principal.ClientId, TokenHash = principal.TokenHash, Reason = "Logged out." },
                _ => true);
        }, null, cancellationToken);

    /// <summary>
    /// Who an access token belongs to. Runs on every request, so it reads the
    /// concurrent maps without taking the engine lock.
    /// </summary>
    public Result<Principal> Authenticate(string? accessToken)
    {
        if (!_started)
            return new BrokerError(ErrorCodes.Unavailable, "The broker is still starting.", ErrorKind.Unavailable);
        if (string.IsNullOrWhiteSpace(accessToken))
            return BrokerError.Unauthorized(ErrorCodes.Unauthenticated, "Send the access token as 'Authorization: Bearer <token>'.");

        var tokenHash = Secrets.Hash(accessToken.Trim());
        if (!_state.Sessions.TryGetValue(tokenHash, out var session)
            || !_state.Apps.TryGetValue(session.AppId, out var app)
            || !_state.Accounts.TryGetValue(session.ClientId, out var account))
            return BrokerError.Unauthorized(ErrorCodes.Unauthenticated, "The access token is not valid. Log in again.");

        if (session.ExpiresAt <= _clock.UtcNow)
            return BrokerError.Unauthorized(ErrorCodes.SessionExpired,
                $"The session ended at {Ist.ToIst(session.ExpiresAt):yyyy-MM-dd HH:mm} IST. Log in again with a fresh TOTP code.");

        return new Principal(session.ClientId, session.AppId, tokenHash, session.ExpiresAt, app.StaticIps, account.Profile);
    }

    private static BrokerError? CheckAmount(decimal amount)
    {
        if (amount <= 0 || amount > MaxTransfer || decimal.Round(amount, 2) != amount)
            return BrokerError.Invalid(ErrorCodes.InvalidAmount, "An amount is a positive number of rupees with at most two decimals.");
        return null;
    }

    private static string CleanReference(string? reference, string fallback)
    {
        var trimmed = reference?.Trim();
        return string.IsNullOrEmpty(trimmed) ? fallback : trimmed.Length > 100 ? trimmed[..100] : trimmed;
    }

    private static Result<IReadOnlyList<string>> NormalizeIps(IReadOnlyList<string> raw, BrokerProfile profile, bool allowNone)
    {
        var ips = new List<string>();
        foreach (var text in raw)
        {
            var ip = Secrets.NormalizeIp(text);
            if (ip is null) return BrokerError.Invalid(ErrorCodes.InvalidIp, $"'{text}' is not an IPv4 or IPv6 address.");
            if (!ips.Contains(ip)) ips.Add(ip);
        }

        if (ips.Count == 0 && !allowNone)
            return BrokerError.Invalid(ErrorCodes.InvalidIp, "Give at least one static IP.");
        if (ips.Count > profile.MaxStaticIpsPerApp)
            return BrokerError.Invalid(ErrorCodes.TooManyIps,
                $"{profile.Name} allows {profile.MaxStaticIpsPerApp} static IP(s) per app; {ips.Count} given.");
        return Result<IReadOnlyList<string>>.Ok(ips);
    }

    private AppView AppViewOf(AppState app, DateTimeOffset now)
    {
        var weekStart = Ist.WeekStart(now);
        DateTimeOffset? sessionEnds = app.ActiveTokenHash is { } hash
                                      && _state.Sessions.TryGetValue(hash, out var session)
                                      && session.ExpiresAt > now
            ? session.ExpiresAt
            : null;
        return new AppView(
            app.AppId,
            app.StaticIps,
            app.StaticIpChanges.Count(at => Ist.DateOf(at) >= weekStart),
            sessionEnds);
    }
}
