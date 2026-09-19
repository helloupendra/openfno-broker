using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using OpenFno.Broker.Application.Abstractions;

namespace OpenFno.Broker.Infrastructure.Hosting;

/// <summary>
/// Encrypts TOTP secrets with ASP.NET Core data protection. The key ring must
/// be kept with the database: without it no stored secret can be read and no
/// account can log in.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("OpenFno.Broker.TotpSecrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}

/// <summary>Runs delayed work on the thread pool until the host stops.</summary>
public sealed class TimerScheduler : IScheduler, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ILogger<TimerScheduler> _logger;

    public TimerScheduler(ILogger<TimerScheduler> logger) => _logger = logger;

    public void Schedule(TimeSpan delay, Func<CancellationToken, Task> work)
    {
        var token = _stopping.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                await work(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled work failed.");
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
