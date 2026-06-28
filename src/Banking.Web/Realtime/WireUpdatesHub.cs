using System.Collections.Concurrent;
using Banking.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Realtime;

[Authorize]
public sealed class WireUpdatesHub(
    IDbContextFactory<BankingDbContext> dbFactory,
    WireUpdateTracker tracker) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http is null
            || !Guid.TryParse(http.Request.Query["wireId"], out var wireId))
        {
            Context.Abort();
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(Context.ConnectionAborted);
        // The hub emits only an opaque ID. The fragment endpoint remains the data authorization boundary.
        if (!await db.WireTransfers.AnyAsync(x => x.Id == wireId, Context.ConnectionAborted))
        {
            Context.Abort();
            return;
        }

        Context.Items[nameof(WireUpdateTracker)] = wireId;
        tracker.Add(wireId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(wireId), Context.ConnectionAborted);
        await Clients.Caller.SendAsync("wireUpdated", wireId, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(nameof(WireUpdateTracker), out var value) && value is Guid wireId)
            tracker.Remove(wireId);
        return base.OnDisconnectedAsync(exception);
    }

    internal static string Group(Guid wireId) => $"wire:{wireId:N}";
}

public sealed class WireUpdateTracker
{
    private readonly ConcurrentDictionary<Guid, int> _subscriptions = new();

    public IReadOnlyCollection<Guid> WireIds => _subscriptions.Keys.ToArray();

    public void Add(Guid wireId) => _subscriptions.AddOrUpdate(wireId, 1, (_, count) => count + 1);

    public void Remove(Guid wireId)
    {
        while (_subscriptions.TryGetValue(wireId, out var count))
        {
            if (count <= 1)
            {
                if (_subscriptions.TryRemove(new KeyValuePair<Guid, int>(wireId, count))) return;
            }
            else if (_subscriptions.TryUpdate(wireId, count - 1, count)) return;
        }
    }
}
