using Npgsql;

namespace Kasa.Api;

internal sealed class ReplenishmentWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<ReplenishmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Clamp(configuration.GetValue("Purchasing:ReplenishmentRefreshSeconds", 10), 1, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var pos = scope.ServiceProvider.GetRequiredService<PosService>();
                var work = await pos.ListReplenishmentWork(stoppingToken);
                foreach (var item in work)
                {
                    try
                    {
                        await pos.RefreshReplenishment(item, stoppingToken);
                    }
                    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
                    {
                        // Another worker committed a newer snapshot. A later poll rechecks whether work is due.
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Replenishment refresh failed for store {Store}, product {Product}: {ErrorType}",
                            item.Store, item.Product, ex.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not list replenishment work: {ErrorType}", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
