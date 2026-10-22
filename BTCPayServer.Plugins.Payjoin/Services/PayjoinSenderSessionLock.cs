using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Serializes one session's replay and external effects across HTTP actions and workers.
/// PostgreSQL transaction-scoped advisory locks also cover other server processes and are
/// released on connection disposal/crash. The durable dispatch marker covers lost responses.
/// This transaction holds only the advisory lock; protocol writes use their own short transactions.
/// </summary>
internal static class PayjoinSenderSessionLock
{
    private static readonly PayjoinSessionBuildLock LocalLocks = new();
    internal const int Capacity = 16;
    private static readonly SemaphoreSlim OwnershipSlots = new(Capacity, Capacity);

    internal static ParallelOptions WorkerOptions(CancellationToken token) => new()
    {
        MaxDegreeOfParallelism = Capacity / 2,
        CancellationToken = token
    };

    internal static async Task<IAsyncDisposable?> TryAcquireAsync(
        PayjoinPluginDbContextFactory factory, string sessionId, CancellationToken cancellationToken)
    {
        var local = await LocalLocks.TryAcquireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (local is null)
            return null;

        PayjoinPluginDbContext? context = null;
        var hasSlot = false;
        try
        {
            context = factory.CreateContext();
            if (context.Database.IsNpgsql())
            {
                hasSlot = await OwnershipSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false);
                if (!hasSlot)
                {
                    await context.DisposeAsync().ConfigureAwait(false);
                    local.Dispose();
                    return null;
                }
                // Long polls must not occupy the same pool needed by the short persistence
                // writes inside this operation. Bound the separate ownership pool as well.
                var connection = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString())
                    { ApplicationName = "Payjoin sender ownership", MaxPoolSize = Capacity };
                context.Database.SetConnectionString(connection.ConnectionString);
                await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                // This ownership transaction must not run through EF's retry strategy: a
                // reconnect would lose the lock while the operation still owns its lease.
                using var command = context.Database.GetDbConnection().CreateCommand();
                command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))";
                command.Parameters.Add(new NpgsqlParameter("key", $"payjoin-sender:{sessionId}"));
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                {
                    await context.DisposeAsync().ConfigureAwait(false);
                    local.Dispose();
                    OwnershipSlots.Release();
                    return null;
                }
            }

            // InMemory/SQLite fixtures use the local guard; production uses PostgreSQL as well.
            return new Lease(context, local, hasSlot);
        }
        catch
        {
            try
            {
                if (context is not null)
                    await context.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                local.Dispose();
                if (hasSlot) OwnershipSlots.Release();
            }
            throw;
        }
    }

    private sealed class Lease(PayjoinPluginDbContext context, IDisposable local, bool hasSlot) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await context.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                local.Dispose();
                if (hasSlot) OwnershipSlots.Release();
            }
        }
    }
}
