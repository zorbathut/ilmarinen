using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// Every priority's rank by id, which is what lists sort priorities by. Shared by every page: read once, and again only after the priorities change.
/// </summary>
public class PriorityRanks
{
    private readonly IServiceScopeFactory _scopeFactory;
    private volatile IReadOnlyDictionary<Guid, int>? _ranks;

    public PriorityRanks(IServiceScopeFactory scopeFactory, UIEventService uiEvents)
    {
        _scopeFactory = scopeFactory;
        uiEvents.OnPrioritiesChanged += () => _ranks = null;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetAsync()
    {
        if (_ranks is { } known)
        {
            return known;
        }
        using var scope = _scopeFactory.CreateScope();
        var ranks = await scope.ServiceProvider.GetRequiredService<PriorityRepository>().GetRanksAsync();

        // Two pages reading at once both load; either answer is current.
        _ranks = ranks;
        return ranks;
    }
}
