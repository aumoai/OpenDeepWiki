using KoalaWiki.Domains.Warehouse;
using Microsoft.EntityFrameworkCore;

namespace KoalaWiki.BackendService;

public partial class WarehouseProcessingTask(IServiceProvider service, ILogger<WarehouseProcessingTask> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken);

        if (!DocumentOptions.EnableIncrementalUpdate)
        {
            logger.LogWarning("Incremental update is not enabled, skipping incremental update task");
            return;
        }

        // Read environment variable to get update interval
        var updateInterval = 5;
        if (int.TryParse(Environment.GetEnvironmentVariable("UPDATE_INTERVAL"), out var interval))
        {
            updateInterval = interval;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Read existing warehouses with status=2
                await using var scope = service.CreateAsyncScope();

                var dbContext = scope.ServiceProvider.GetService<IKoalaWikiContext>();

                // Read existing warehouses with status=2, sync enabled, and processing time meets one week
                var warehouse = await dbContext!.Warehouses
                    .Where(x => x.Status == WarehouseStatus.Completed && x.EnableSync)
                    .FirstOrDefaultAsync(stoppingToken);

                if (warehouse == null)
                {
                    // If no warehouses, wait for a while then retry
                    await Task.Delay(1000 * 60, stoppingToken);
                    continue;
                }

                var documents = await dbContext.Documents
                    .Where(x => warehouse.Id == x.WarehouseId && x.LastUpdate < DateTime.Now.AddDays(-updateInterval))
                    .ToListAsync(stoppingToken);

                var warehouseIds = documents.Select(x => x.WarehouseId).ToArray();

                // From here we get warehouses that haven't been updated for over a week
                warehouse = await dbContext.Warehouses
                    .Where(x => warehouseIds.Contains(x.Id))
                    .FirstOrDefaultAsync(stoppingToken);

                if (warehouse == null)
                {
                    await Task.Delay(1000 * 60, stoppingToken);
                }
                else
                {
                    var document = documents.FirstOrDefault(x => x.WarehouseId == warehouse.Id);

                    // Create sync record
                    var syncRecord = new WarehouseSyncRecord
                    {
                        Id = Guid.NewGuid().ToString(),
                        WarehouseId = warehouse.Id,
                        Status = WarehouseSyncStatus.InProgress,
                        StartTime = DateTime.UtcNow,
                        FromVersion = warehouse.Version,
                        FileCount = documents.Count,
                        Trigger = WarehouseSyncTrigger.Auto
                    };

                    await dbContext.WarehouseSyncRecords.AddAsync(syncRecord, stoppingToken);
                    await dbContext.SaveChangesAsync(stoppingToken);

                    try
                    {
                        var commitId = await HandleAnalyseAsync(warehouse, document, dbContext);

                        if (string.IsNullOrEmpty(commitId))
                        {
                            // Sync failed, update record status
                            syncRecord.Status = WarehouseSyncStatus.Failed;
                            syncRecord.EndTime = DateTime.UtcNow;
                            syncRecord.ErrorMessage = "Failed to get new commit ID during sync";

                            // Update git record
                            await dbContext.Documents
                                .Where(x => x.WarehouseId == warehouse.Id)
                                .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastUpdate, DateTime.Now), stoppingToken);

                            await dbContext.SaveChangesAsync(stoppingToken);
                            return;
                        }

                        // Sync successful, update record status
                        syncRecord.Status = WarehouseSyncStatus.Success;
                        syncRecord.EndTime = DateTime.UtcNow;
                        syncRecord.ToVersion = commitId;

                        // Update git record
                        await dbContext.Documents
                            .Where(x => x.WarehouseId == warehouse.Id)
                            .ExecuteUpdateAsync(x => x.SetProperty(a => a.LastUpdate, DateTime.Now), stoppingToken);

                        await dbContext.Warehouses.Where(x => x.Id == warehouse.Id)
                            .ExecuteUpdateAsync(x => x.SetProperty(a => a.Version, commitId), stoppingToken);

                        await dbContext.SaveChangesAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        // Sync exception, update record status
                        syncRecord.Status = WarehouseSyncStatus.Failed;
                        syncRecord.EndTime = DateTime.UtcNow;
                        syncRecord.ErrorMessage = ex.Message;
                        await dbContext.SaveChangesAsync(stoppingToken);
                        throw;
                    }
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process warehouse");

                await Task.Delay(1000 * 60, stoppingToken);
            }
        }
    }
}