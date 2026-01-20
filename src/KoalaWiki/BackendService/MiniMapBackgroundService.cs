using System.Text.Json;

namespace KoalaWiki.BackendService;

/// <summary>
/// Mind map service generation
/// </summary>
/// <param name="service"></param>
public sealed class MiniMapBackgroundService(IServiceProvider service) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken); // Wait for service startup to complete

        using var scope = service.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IKoalaWikiContext>();


        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var existingMiniMapIds = await context.MiniMaps
                    .Select(m => m.WarehouseId)
                    .ToListAsync(stoppingToken);

                // Query warehouses that need knowledge graph generation
                var query = context.Warehouses
                    .Where(w => w.Status == WarehouseStatus.Completed && !existingMiniMapIds.Contains(w.Id))
                    .OrderBy(w => w.CreatedAt)
                    .AsNoTracking();

                var item = await query.FirstOrDefaultAsync(stoppingToken);
                if (item == null)
                {
                    await Task.Delay(10000, stoppingToken); // Wait 10 seconds before retry
                    continue;
                }

                Log.Logger.Information("Starting knowledge graph generation, {Count} warehouses need processing",
                    await query.CountAsync(cancellationToken: stoppingToken));

                try
                {
                    Log.Logger.Information("Starting to process warehouse {WarehouseName}", item.Name);

                    var document = await context.Documents
                        .Where(d => d.WarehouseId == item.Id)
                        .FirstOrDefaultAsync(stoppingToken);

                    var miniMap = await MiniMapService.GenerateMiniMap(document.GetCatalogueSmartFilterOptimized(),
                        item, document.GitPath);
                    if (miniMap != null)
                    {
                        context.MiniMaps.Add(new MiniMap
                        {
                            WarehouseId = item.Id,
                            Value = JsonSerializer.Serialize(miniMap, JsonSerializerOptions.Web),
                            CreatedAt = DateTime.UtcNow,
                            Id = Guid.NewGuid().ToString("N")
                        });
                        await context.SaveChangesAsync(stoppingToken);
                        Log.Logger.Information("Knowledge graph generation succeeded for warehouse {WarehouseName}", item.Name);
                    }
                    else
                    {
                        Log.Logger.Warning("Knowledge graph generation failed for warehouse {WarehouseName}", item.Name);
                    }
                }
                catch (Exception e)
                {
                    Log.Logger.Error(e, "Exception occurred while processing warehouse {WarehouseName}", item.Name);
                }
            }
            catch (Exception e)
            {
                await Task.Delay(10000, stoppingToken); // Wait 10 seconds before retry
                Log.Logger.Error("MiniMapBackgroundService execution exception: {Message}", e.Message);
            }
        }
    }
}