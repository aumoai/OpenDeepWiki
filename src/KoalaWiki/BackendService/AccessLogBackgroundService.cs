namespace KoalaWiki.BackendService;

/// <summary>
/// Access log background processing service
/// </summary>
public class AccessLogBackgroundService(
    IServiceProvider serviceProvider,
    AccessLogQueue logQueue,
    ILogger<AccessLogBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Access log background processing service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var logEntry = await logQueue.DequeueAsync(stoppingToken);
                if (logEntry != null)
                {
                    await ProcessLogEntryAsync(logEntry);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation operation, exit loop
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred while processing access log");
                
                // Wait for a while before continuing when error occurs
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Access log background processing service stopped");
    }

    private async Task ProcessLogEntryAsync(AccessLogEntry logEntry)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var statisticsService = scope.ServiceProvider.GetService<StatisticsService>();
            
            if (statisticsService != null)
            {
                await statisticsService.RecordAccessAsync(
                    resourceType: logEntry.ResourceType,
                    resourceId: logEntry.ResourceId,
                    userId: logEntry.UserId,
                    ipAddress: logEntry.IpAddress,
                    userAgent: logEntry.UserAgent,
                    path: logEntry.Path,
                    method: logEntry.Method,
                    statusCode: logEntry.StatusCode,
                    responseTime: logEntry.ResponseTime
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process access log entry: {ResourceType}/{ResourceId}, Path: {Path}", 
                logEntry.ResourceType, logEntry.ResourceId, logEntry.Path);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping access log background processing service...");
        
        // Process remaining log entries in queue
        var remainingCount = logQueue.Count;
        if (remainingCount > 0)
        {
            logger.LogInformation("Processing {Count} remaining access log entries in queue", remainingCount);
            
            var timeout = TimeSpan.FromSeconds(30); // Maximum wait 30 seconds
            var endTime = DateTime.UtcNow.Add(timeout);
            
            while (logQueue.Count > 0 && DateTime.UtcNow < endTime)
            {
                try
                {
                    var logEntry = await logQueue.DequeueAsync(cancellationToken);
                    if (logEntry != null)
                    {
                        await ProcessLogEntryAsync(logEntry);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process access log during stop");
                }
            }
        }

        await base.StopAsync(cancellationToken);
    }
} 