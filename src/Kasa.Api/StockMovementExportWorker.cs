namespace Kasa.Api;

internal sealed class StockMovementExportWorker(IServiceScopeFactory scopes, ILogger<StockMovementExportWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var pos = scope.ServiceProvider.GetRequiredService<PosService>();
                for (var i = 0; i < 10 && await pos.ProcessStockMovementExport(stoppingToken); i++)
                {
                    // Bound the number of jobs picked between poll ticks.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Stock movement export worker failed: {ErrorType}", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
