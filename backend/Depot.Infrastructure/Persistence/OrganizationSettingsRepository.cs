using Depot.Application.Organization;
using Depot.Domain.Common;
using Depot.Domain.Organization;
using Depot.Infrastructure.Organization;
using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Depot.Infrastructure.Persistence;

public sealed class OrganizationSettingsRepository(
    DepotDbContext dbContext,
    IOptions<OrganizationOptions> defaults,
    IPathEnvironment environment) : IOrganizationSettingsRepository
{
    public async Task<StoredOrganizationSettings> GetAsync(CancellationToken cancellationToken)
    {
        var record = await dbContext.OrganizationSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.Id == OrganizationSettingsRecord.SingletonId, cancellationToken);
        if (record is null)
        {
            return new StoredOrganizationSettings(FromDefaults(defaults.Value), IsSaved: false);
        }

        var mappings = await dbContext.OrganizationPathMappings
            .AsNoTracking()
            .OrderBy(mapping => mapping.Position)
            .Select(mapping => new { mapping.QbittorrentPath, mapping.DepotPath })
            .ToListAsync(cancellationToken);

        var settings = OrganizationSettings.Create(
            record.MoviesPath,
            record.SeriesPath,
            record.ImportMode,
            record.PollingIntervalSeconds,
            mappings.Select(mapping => ((string?)mapping.QbittorrentPath, (string?)mapping.DepotPath)).ToList(),
            environment.HomeDirectory);
        return new StoredOrganizationSettings(settings, IsSaved: true);
    }

    public async Task SaveAsync(OrganizationSettings settings, CancellationToken cancellationToken)
    {
        var record = await dbContext.OrganizationSettings
            .SingleOrDefaultAsync(existing => existing.Id == OrganizationSettingsRecord.SingletonId, cancellationToken);
        if (record is null)
        {
            dbContext.OrganizationSettings.Add(new OrganizationSettingsRecord
            {
                Id = OrganizationSettingsRecord.SingletonId,
                MoviesPath = settings.MoviesPath,
                SeriesPath = settings.SeriesPath,
                ImportMode = settings.ImportMode,
                PollingIntervalSeconds = settings.PollingIntervalSeconds,
            });
        }
        else
        {
            record.MoviesPath = settings.MoviesPath;
            record.SeriesPath = settings.SeriesPath;
            record.ImportMode = settings.ImportMode;
            record.PollingIntervalSeconds = settings.PollingIntervalSeconds;
        }

        // The mappings are replaced as a whole, in the same save as the settings.
        dbContext.OrganizationPathMappings.RemoveRange(await dbContext.OrganizationPathMappings.ToListAsync(cancellationToken));
        dbContext.OrganizationPathMappings.AddRange(settings.PathMappings.Select((mapping, position) => new OrganizationPathMappingRecord
        {
            Position = position,
            QbittorrentPath = mapping.QbittorrentPath,
            DepotPath = mapping.DepotPath,
        }));

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private OrganizationSettings FromDefaults(OrganizationOptions options)
    {
        try
        {
            return OrganizationSettings.Create(
                options.MoviesPath,
                options.SeriesPath,
                options.ImportMode,
                options.PollingIntervalSeconds,
                options.PathMappings.Select(mapping => (mapping.QbittorrentPath, mapping.DepotPath)).ToList(),
                environment.HomeDirectory);
        }
        catch (DomainValidationException exception)
        {
            throw new InvalidOperationException(
                $"The \"{OrganizationOptions.SectionName}\" defaults in the configuration are invalid ({exception.Field}): {exception.Message}");
        }
    }
}
