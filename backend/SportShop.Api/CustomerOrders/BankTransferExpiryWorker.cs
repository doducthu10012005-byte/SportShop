using Microsoft.Extensions.Options;

namespace SportShop.Api.CustomerOrders;

internal sealed class BankTransferExpiryOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 5;
}

internal sealed class BankTransferExpiryWorker(BankTransferExpirySweep sweep, IOptions<BankTransferExpiryOptions> options,
    IHostEnvironment environment, ILogger<BankTransferExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("Testing") || !options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await sweep.Run(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    var cause = ex.GetBaseException();
                    logger.LogError("Bank transfer expiry scan failed ({ErrorType}, SQL error {SqlError}).",
                        cause.GetType().Name, (cause as Microsoft.Data.SqlClient.SqlException)?.Number);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
