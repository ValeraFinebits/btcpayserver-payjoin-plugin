using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Data;
using Npgsql;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinReceiverSessionStore
{
    private static readonly Action<ILogger, string, int, Exception?> LogConcurrencyRetry =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(1, nameof(LogConcurrencyRetry)),
            "Payjoin session write {Operation} lost a concurrency race on attempt {Attempt}; re-reading and retrying.");
    private static readonly Action<ILogger, string, int, Exception?> LogConcurrencyRetriesExhausted =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(2, nameof(LogConcurrencyRetriesExhausted)),
            "Payjoin session write {Operation} still lost a concurrency race after {Attempt} attempts; giving up.");

    private readonly PayjoinPluginDbContextFactory _pluginDbContextFactory;
    private readonly IPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector;
    private readonly PayjoinMailroomManager? _mailroomManager;
    private readonly ILogger<PayjoinReceiverSessionStore>? _logger;
    private readonly IPayjoinInvoiceEventPublisher? _invoiceEvents;

    internal PayjoinReceiverSessionStore(
        PayjoinPluginDbContextFactory pluginDbContextFactory,
        IPayjoinUniqueConstraintViolationDetector uniqueConstraintViolationDetector,
        PayjoinMailroomManager? mailroomManager = null,
        ILogger<PayjoinReceiverSessionStore>? logger = null,
        IPayjoinInvoiceEventPublisher? invoiceEvents = null)
    {
        ArgumentNullException.ThrowIfNull(pluginDbContextFactory);
        ArgumentNullException.ThrowIfNull(uniqueConstraintViolationDetector);
        _pluginDbContextFactory = pluginDbContextFactory;
        _uniqueConstraintViolationDetector = uniqueConstraintViolationDetector;
        _mailroomManager = mailroomManager;
        _logger = logger;
        _invoiceEvents = invoiceEvents;
    }

    internal PayjoinReceiverSessionState GetOrCreateSession(
        string invoiceId,
        string receiverAddress,
        string storeId,
        DateTimeOffset monitoringExpiresAt,
        IEnumerable<string> bootstrapEvents,
        CreatePayjoinAccountingBridgeRequest? bridgeRequest = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrapEvents);

        var persistedEvents = bootstrapEvents.ToArray();
        if (persistedEvents.Length == 0)
        {
            throw new ArgumentException("Bootstrap events must contain the initial receiver session state.", nameof(bootstrapEvents));
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var strategyContext = _pluginDbContextFactory.CreateContext();
                return strategyContext.Database.CreateExecutionStrategy().Execute(() =>
                    CreateSessionCore(invoiceId, receiverAddress, storeId, monitoringExpiresAt, persistedEvents, bridgeRequest));
            }
            catch (Exception ex) when (attempt < MaxConcurrencyRetries &&
                (ex is DbUpdateConcurrencyException || IsSerializationConflict(ex) ||
                 ex is DbUpdateException update && (IsReceiverSessionConflict(update) ||
                     _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(update, PayjoinPluginDbSchema.AccountingBridgesInvoiceIdIndex))))
            {
                // Reload the winning pair, never apply the losing request's amount to it.
            }
        }
    }

    private PayjoinReceiverSessionState CreateSessionCore(string invoiceId, string receiverAddress, string storeId,
        DateTimeOffset monitoringExpiresAt, string[] persistedEvents, CreatePayjoinAccountingBridgeRequest? bridgeRequest)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        using var transaction = context.Database.IsRelational()
            ? context.Database.BeginTransaction() : null;
        var sessionData = LoadSessionWithEvents(context, invoiceId);
        var now = DateTimeOffset.UtcNow;

        if (sessionData is not null)
        {
            return CreateState(sessionData, OrderedEvents(sessionData));
        }
        if (bridgeRequest is not null)
        {
            using var replay = PayjoinMethods.ReplayReceiverEventLog(new SnapshotPersister(persistedEvents));
            using var history = replay.SessionHistory();
            using var uri = history.PjUri();
            var pin = uri.AmountSats();
            if (pin is not > 0 or > long.MaxValue)
                throw new PayjoinAccountingReconciliationDataException("A new receiver session requires a positive native URI amount.");
            var bridge = context.AccountingBridges.SingleOrDefault(x => x.InvoiceId == invoiceId);
            if (bridge is not null && !IsEmptyPendingBridge(bridge))
                throw new PayjoinAccountingReconciliationDataException("Existing Payjoin accounting data requires manual recovery.");
            if (bridge is null)
            {
                bridge = new PayjoinAccountingBridgeData
                {
                    InvoiceId = invoiceId,
                    StoreId = storeId,
                    CryptoCode = bridgeRequest.CryptoCode,
                    PaymentMethodId = bridgeRequest.PaymentMethodId,
                    CreatedAt = now,
                    Status = PayjoinAccountingBridgeStatus.PendingFallback
                };
                context.AccountingBridges.Add(bridge);
            }
            bridge.EffectiveInvoiceValueSats = checked((long)pin.Value);
            bridge.ExpiresAt = monitoringExpiresAt;
            bridge.UpdatedAt = PayjoinAccountingBridgeService.NextWriteTime(bridge, now);
        }
        sessionData = new PayjoinReceiverSessionData
        {
            InvoiceId = invoiceId,
            StoreId = storeId,
            ReceiverAddress = receiverAddress,
            MonitoringExpiresAt = monitoringExpiresAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.ReceiverSessions.Add(sessionData);

        AddBootstrapEvents(context, invoiceId, persistedEvents, now);

        context.SaveChanges();
        transaction?.Commit();
        if (bridgeRequest is not null)
        {
            _invoiceEvents?.Publish(PayjoinInvoiceEventKind.TrackingStarted, invoiceId);
        }
        return CreateState(sessionData, persistedEvents);
    }

    internal void StorePayjoinUri(string invoiceId, string receiverAddress, string payjoinUri, Guid? sessionInstanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payjoinUri);

        WriteWithConcurrencyRetry(context =>
        {
            var sessionData = LoadSessionDataCore(context, invoiceId, asNoTracking: false);
            CheckInstance(sessionData, ref sessionInstanceId);
            if (sessionData is null || sessionData.PayjoinUri is not null)
            {
                return false;
            }

            if (!string.Equals(sessionData.ReceiverAddress, receiverAddress, StringComparison.Ordinal))
            {
                return false;
            }

            if (!context.ReceiverSessionEvents.Any(x => x.InvoiceId == invoiceId))
            {
                return false;
            }

            sessionData.PayjoinUri = payjoinUri;
            sessionData.UpdatedAt = DateTimeOffset.UtcNow;
            context.SaveChanges();
            return true;
        });
    }

    public bool TryGetSession(string invoiceId, out PayjoinReceiverSessionState? session)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return TryLoadSessionCore(context, invoiceId, out session);
    }

    internal PayjoinReceiverSessionState GetAttempt(string invoiceId, Guid sessionInstanceId)
    {
        if (!TryGetSession(invoiceId, out var state) || state!.SessionInstanceId != sessionInstanceId)
            throw new PayjoinSessionStaleException();
        return state;
    }

    internal static JsonReceiverSessionPersister Snapshot(PayjoinReceiverSessionState session) => new SnapshotPersister(session.GetEvents());

    internal sealed record PaymentSnapshot(PayjoinReceiverSessionState Session, PayjoinAccountingBridgeState? Bridge);

    internal PaymentSnapshot ReadPaymentSnapshot(DatabaseBackedReceiverPersister owner)
    {
        using var strategyContext = _pluginDbContextFactory.CreateContext();
        return strategyContext.Database.CreateExecutionStrategy().Execute(() => ReadPaymentSnapshotCore(owner));
    }

    private PaymentSnapshot ReadPaymentSnapshotCore(DatabaseBackedReceiverPersister owner)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        using var snapshot = context.Database.IsRelational()
            ? context.Database.BeginTransaction(context.Database.IsNpgsql() ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable) : null;
        var row = LoadSessionWithEvents(context, owner.InvoiceId) ?? throw new PayjoinSessionStaleException();
        CheckOwner(owner, row);
        var bridge = context.AccountingBridges.AsNoTracking().SingleOrDefault(x => x.InvoiceId == owner.InvoiceId);
        return new PaymentSnapshot(CreateState(row, OrderedEvents(row)), bridge is null ? null : PayjoinAccountingBridgeService.ToState(bridge));
    }

    private static void CheckInstance(PayjoinReceiverSessionData? session, ref Guid? expected)
    {
        if (expected.HasValue && (session is null || session.SessionInstanceId != expected.Value))
            throw new PayjoinSessionStaleException();
        if (session is not null) expected ??= session.SessionInstanceId;
    }

    private static bool IsSerializationConflict(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } ||
        exception.InnerException is { } inner && IsSerializationConflict(inner);

    private static bool IsEmptyPendingBridge(PayjoinAccountingBridgeData bridge) =>
        bridge.Status == PayjoinAccountingBridgeStatus.PendingFallback &&
        bridge.FallbackTransactionId is null && bridge.FallbackOutputIndex is null && bridge.FallbackValueSats is null &&
        bridge.SettlementScript is null && bridge.SettlementKeyPath is null && bridge.ExpectedFinalTransactionId is null &&
        bridge.ExpectedFinalOutputIndex is null && bridge.ExpectedFinalValueSats is null && bridge.ReconciledAt is null && bridge.FailureMessage is null;

    internal enum EarlyRejectionResult { Rejected, StaleOrAdvanced, RequiresRecovery }

    internal EarlyRejectionResult TryRejectEarlyOriginal(PayjoinReceiverSessionState checkedState, byte[] receiverScript) =>
        WriteWithConcurrencyRetry(context =>
        {
            var row = LoadSessionWithEvents(context, checkedState.InvoiceId);
            if (row is null || row.SessionInstanceId != checkedState.SessionInstanceId)
                return EarlyRejectionResult.StaleOrAdvanced;
            if (row.AttemptVersion != checkedState.AttemptVersion) return EarlyRejectionResult.StaleOrAdvanced;
            const EarlyRejectionResult unsafeResult = EarlyRejectionResult.RequiresRecovery;
            try
            {
                using var replay = PayjoinMethods.ReplayReceiverEventLog(new SnapshotPersister(OrderedEvents(row)));
                using var state = replay.State();
                if (state is ReceiveSession.Initialized) return EarlyRejectionResult.StaleOrAdvanced;
                if (state is not ReceiveSession.WantsOutputs) return unsafeResult;
                using var history = replay.SessionHistory();
                using var uri = history.PjUri();
                var pin = uri.AmountSats();
                var original = history.FallbackTx();
                if (pin is not > 0 or > long.MaxValue || original is not { Length: > 0 }) return unsafeResult;
                var bridge = context.AccountingBridges.SingleOrDefault(x => x.InvoiceId == row.InvoiceId);
                if (bridge?.Status is PayjoinAccountingBridgeStatus.Failed or PayjoinAccountingBridgeStatus.Expired or PayjoinAccountingBridgeStatus.Reconciled)
                    return EarlyRejectionResult.StaleOrAdvanced;
                if (bridge is null || !IsEmptyPendingBridge(bridge) || bridge.EffectiveInvoiceValueSats != checked((long)pin.Value) ||
                    row.ContributedInputTransactionId is not null || row.ContributedInputOutputIndex is not null ||
                    context.ReceiverInputReservations.Any(x => x.InvoiceId == row.InvoiceId)) return unsafeResult;
                var transaction = NBitcoin.Transaction.Load(original, NBitcoin.Network.Main);
                if (PayjoinReceiverPaymentValidation.ValidateOriginalAmount(pin, transaction, receiverScript) is not null)
                    return EarlyRejectionResult.StaleOrAdvanced;
                RemoveAllSessionEvents(context, row);
                context.AccountingBridges.Remove(bridge);
                context.ReceiverSessions.Remove(row);
                context.SaveChanges();
                _mailroomManager?.ClearSessionFailedRelays(row.InvoiceId);
                return EarlyRejectionResult.Rejected;
            }
            catch (UniffiException ex)
            {
                (ex as IDisposable)?.Dispose();
                return unsafeResult;
            }
        });

    internal bool TryCompleteUnsignedAttempt(string invoiceId, Guid sessionInstanceId, int expectedVersion, byte[] receiverScript) =>
        WriteWithConcurrencyRetry(context =>
        {
            var row = LoadSessionWithEvents(context, invoiceId);
            if (row is null || row.SessionInstanceId != sessionInstanceId || row.AttemptVersion != expectedVersion)
                return false;
            try
            {
                using var replay = PayjoinMethods.ReplayReceiverEventLog(new SnapshotPersister(OrderedEvents(row)));
                using var state = replay.State();
                if (state is not (ReceiveSession.WantsOutputs or ReceiveSession.WantsInputs)) return false;
                using var history = replay.SessionHistory();
                using var uri = history.PjUri();
                var pin = uri.AmountSats();
                var original = history.FallbackTx();
                if (pin is not > 0 or > long.MaxValue || original is not { Length: > 0 }) return false;
                var transaction = NBitcoin.Transaction.Load(original, NBitcoin.Network.Main);
                var payment = PayjoinReceiverPaymentValidation.ValidateOriginalAmount(pin, transaction, receiverScript);
                if (payment is null) return false;
                var bridge = context.AccountingBridges.SingleOrDefault(x => x.InvoiceId == invoiceId);
                if (bridge is null ||
                    bridge.Status is not (PayjoinAccountingBridgeStatus.PendingFallback or PayjoinAccountingBridgeStatus.PendingFinalTransaction) ||
                    bridge.FailureMessage is not null || bridge.ReconciledAt is not null ||
                    bridge.ExpectedFinalTransactionId is not null || bridge.ExpectedFinalOutputIndex is not null || bridge.ExpectedFinalValueSats is not null ||
                    bridge.EffectiveInvoiceValueSats != payment.AmountSats || bridge.FallbackValueSats != payment.AmountSats ||
                    bridge.FallbackTransactionId != payment.TransactionId || bridge.FallbackOutputIndex != payment.OutputIndex ||
                    row.ContributedInputTransactionId is not null || row.ContributedInputOutputIndex is not null ||
                    context.ReceiverInputReservations.Any(x => x.InvoiceId == invoiceId)) return false;

                if (state is ReceiveSession.WantsOutputs)
                {
                    if (bridge.SettlementScript is not null || bridge.SettlementKeyPath is not null ||
                        PayjoinReceiverPaymentValidation.ReadCommittedOutputs(OrderedEvents(row)) is not null) return false;
                }
                else if (!PayjoinReceiverPaymentValidation.CommittedOutputsAgree(PayjoinReceiverPaymentValidation.ReadCommittedOutputs(OrderedEvents(row)), bridge.SettlementScript, checked((long)pin.Value))) return false;

                RemoveAllSessionEvents(context, row);
                context.AccountingBridges.Remove(bridge);
                context.ReceiverSessions.Remove(row);
                context.SaveChanges();
                _mailroomManager?.ClearSessionFailedRelays(invoiceId);
                return true;
            }
            catch (ReceiverReplayException ex)
            {
                ex.Dispose();
                return false;
            }
        });

    [SuppressMessage("Design", "CA1055:URI-like return values should not be strings", Justification = "A bitcoin: BIP21 URI is stored and merged as text; System.Uri would re-encode its query parameters.")]
    internal string? GetServablePayjoinUri(string invoiceId, string destination)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var row = context.ReceiverSessions
            .AsNoTracking()
            .Where(x => x.InvoiceId == invoiceId && x.PayjoinUri != null &&
                        !context.AccountingBridges.Any(b => b.InvoiceId == x.InvoiceId && (b.Status == PayjoinAccountingBridgeStatus.Failed || b.ExpectedFinalTransactionId != null || b.SettlementScript != null)))
            .Select(x => new
            {
                x.PayjoinUri,
                x.ReceiverAddress,
                x.MonitoringExpiresAt,
                x.IsCloseRequested
            })
            .FirstOrDefault();

        if (row is null)
        {
            return null;
        }

        var servability = new PayjoinSessionServability(
            HasEvents: true,
            row.IsCloseRequested,
            row.MonitoringExpiresAt,
            row.ReceiverAddress);

        return servability.Decide(destination) == PayjoinPersistedSessionDecision.Reuse
            ? row.PayjoinUri
            : null;
    }

    public IReadOnlyCollection<PayjoinReceiverSessionState> GetSessions()
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return context.Database.CreateExecutionStrategy().Execute(() => LoadPollableSessions(context));
    }

    private const int MaxConcurrencyRetries = 3;

    internal T WriteWithConcurrencyRetry<T>(Func<PayjoinPluginDbContext, T> write, [CallerMemberName] string operation = "")
    {
        for (var attempt = 1; ; attempt++)
        {
            using var context = _pluginDbContextFactory.CreateContext();
            try
            {
                return write(context);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                if (attempt >= MaxConcurrencyRetries)
                {
                    if (_logger is not null)
                    {
                        LogConcurrencyRetriesExhausted(_logger, operation, attempt, ex);
                    }

                    throw;
                }

                if (_logger is not null)
                {
                    LogConcurrencyRetry(_logger, operation, attempt, ex);
                }
            }
        }
    }

    public bool RemoveSession(string invoiceId, Guid? sessionInstanceId = null, int? expectedVersion = null) => WriteWithConcurrencyRetry(context =>
    {
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        CheckInstance(sessionData, ref sessionInstanceId);
        if (expectedVersion.HasValue && sessionData?.AttemptVersion != expectedVersion.Value) throw new PayjoinSessionStaleException();
        if (sessionData is null)
        {
            _mailroomManager?.ClearSessionFailedRelays(invoiceId);
            return false;
        }

        if (MustRetainHistoryForAccounting(context, invoiceId))
        {
            return false;
        }

        RemoveAllSessionEvents(context, sessionData);
        RemoveInputReservations(context, invoiceId);
        context.ReceiverSessions.Remove(sessionData);
        context.SaveChanges();
        _mailroomManager?.ClearSessionFailedRelays(invoiceId);
        return true;
    });

    private static void RemoveInputReservations(PayjoinPluginDbContext context, string invoiceId)
    {
        var inputReservations = context.ReceiverInputReservations
            .Where(x => x.InvoiceId == invoiceId)
            .ToArray();
        if (inputReservations.Length > 0)
        {
            context.ReceiverInputReservations.RemoveRange(inputReservations);
        }
    }

    internal bool TryRemoveSessionUnlessNegotiating(string invoiceId, Guid? sessionInstanceId = null) => WriteWithConcurrencyRetry(context =>
    {
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        CheckInstance(sessionData, ref sessionInstanceId);
        if (sessionData is null)
        {
            _mailroomManager?.ClearSessionFailedRelays(invoiceId);
            return true;
        }

        if (MustRetainHistoryForAccounting(context, invoiceId))
        {
            return false;
        }

        if (sessionData.ContributedInputTransactionId is not null || sessionData.ContributedInputOutputIndex is not null)
        {
            return false;
        }

        RemoveAllSessionEvents(context, sessionData);
        RemoveInputReservations(context, invoiceId);
        context.ReceiverSessions.Remove(sessionData);
        context.SaveChanges();
        _mailroomManager?.ClearSessionFailedRelays(invoiceId);
        return true;
    });

    public bool RequestClose(string invoiceId, InvoiceStatus invoiceStatus, Guid? sessionInstanceId = null) => WriteWithConcurrencyRetry(context =>
    {
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        CheckInstance(sessionData, ref sessionInstanceId);
        if (sessionData is null)
        {
            return false;
        }

        if (sessionData.IsCloseRequested && sessionData.CloseInvoiceStatus == invoiceStatus)
        {
            return false;
        }

        var requestedAt = DateTimeOffset.UtcNow;
        sessionData.IsCloseRequested = true;
        sessionData.CloseInvoiceStatus = invoiceStatus;
        sessionData.CloseRequestedAt = requestedAt;
        sessionData.InitializedPollAfterCloseRequestConsumed = false;
        sessionData.UpdatedAt = requestedAt;
        context.SaveChanges();
        return true;
    });

    internal bool TryReserveContributedInput(
        DatabaseBackedReceiverPersister owner,
        string storeId,
        NBitcoin.OutPoint outPoint,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(outPoint);
        return WriteWithConcurrencyRetry(context => ReserveContributedInputCore(context, owner, storeId, outPoint, expiresAt));
    }

    private bool ReserveContributedInputCore(
        PayjoinPluginDbContext context,
        DatabaseBackedReceiverPersister owner,
        string storeId,
        NBitcoin.OutPoint outPoint,
        DateTimeOffset expiresAt)
    {
        var invoiceId = owner.InvoiceId;
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId)
            ?? throw new PayjoinSessionStaleException();
        CheckOwner(owner, sessionData);

        var transactionId = outPoint.Hash.ToString();
        var outputIndex = checked((long)outPoint.N);
        if (!string.Equals(sessionData.StoreId, storeId, StringComparison.Ordinal))
        {
            return false;
        }

        var existingReservation = context.ReceiverInputReservations
            .SingleOrDefault(x => x.TransactionId == transactionId && x.OutputIndex == outputIndex);
        if (existingReservation is not null)
        {
            if (string.Equals(existingReservation.InvoiceId, invoiceId, StringComparison.Ordinal))
            {
                return Reclaim(sessionData.ContributedInputTransactionId == transactionId &&
                       sessionData.ContributedInputOutputIndex == outputIndex);
            }

            return false;
        }

        if (sessionData.ContributedInputTransactionId is not null || sessionData.ContributedInputOutputIndex is not null)
        {
            return Reclaim(sessionData.ContributedInputTransactionId == transactionId &&
                   sessionData.ContributedInputOutputIndex == outputIndex);
        }

        bool Reclaim(bool matches)
        {
            if (!matches) return false;
            sessionData.AttemptVersion = checked(sessionData.AttemptVersion + 1);
            context.SaveChanges();
            ConfirmWrite(owner, sessionData);
            return true;
        }

        var reservedAt = DateTimeOffset.UtcNow;
        sessionData.ContributedInputTransactionId = transactionId;
        sessionData.ContributedInputOutputIndex = outputIndex;
        sessionData.UpdatedAt = reservedAt;
        sessionData.AttemptVersion = checked(sessionData.AttemptVersion + 1);
        context.ReceiverInputReservations.Add(new PayjoinReceiverInputReservationData
        {
            InvoiceId = invoiceId,
            StoreId = storeId,
            TransactionId = transactionId,
            OutputIndex = outputIndex,
            ReservedAt = reservedAt,
            ExpiresAt = expiresAt
        });

        try
        {
            context.SaveChanges();
            ConfirmWrite(owner, sessionData);
            return true;
        }
        catch (DbUpdateException ex) when (IsReceiverInputReservationConflict(ex))
        {
            context.ChangeTracker.Clear();

            var winningReservation = context.ReceiverInputReservations
                .AsNoTracking()
                .SingleOrDefault(x => x.TransactionId == transactionId && x.OutputIndex == outputIndex);
            if (winningReservation is null ||
                !string.Equals(winningReservation.InvoiceId, invoiceId, StringComparison.Ordinal))
            {
                return false;
            }

            var winningSession = context.ReceiverSessions
                .AsNoTracking()
                .SingleOrDefault(x => x.InvoiceId == invoiceId);
            if (winningSession is null) throw new PayjoinSessionStaleException();
            CheckOwner(owner, winningSession);
            return winningSession.ContributedInputTransactionId == transactionId &&
                   winningSession.ContributedInputOutputIndex == outputIndex;
        }
    }

    public int CleanupExpiredInputReservations(DateTimeOffset now)
    {
        Dictionary<string, Guid>? owners = null;
        return WriteWithConcurrencyRetry(context =>
    {
        var impactedSessions = context.ReceiverSessions
            .Where(x => x.InputReservations.Any(r => r.ExpiresAt <= now))
            .Include(x => x.InputReservations.Where(r => r.ExpiresAt <= now))
            .AsSingleQuery().ToArray();
        owners ??= impactedSessions.ToDictionary(x => x.InvoiceId, x => x.SessionInstanceId, StringComparer.Ordinal);
        if (impactedSessions.Any(x => !owners.TryGetValue(x.InvoiceId, out var owner) || owner != x.SessionInstanceId))
            throw new PayjoinSessionStaleException();
        var expiredReservations = impactedSessions.SelectMany(x => x.InputReservations).ToArray();
        if (expiredReservations.Length == 0)
        {
            return 0;
        }

        foreach (var sessionData in impactedSessions)
        {
            sessionData.AttemptVersion = checked(sessionData.AttemptVersion + 1);
            if (sessionData.InputReservations.Any(r =>
                string.Equals(sessionData.ContributedInputTransactionId, r.TransactionId, StringComparison.Ordinal) &&
                sessionData.ContributedInputOutputIndex == r.OutputIndex))
            {
                sessionData.ContributedInputTransactionId = null;
                sessionData.ContributedInputOutputIndex = null;
                sessionData.UpdatedAt = now;
            }
        }

        context.ReceiverInputReservations.RemoveRange(expiredReservations);
        context.SaveChanges();
        return expiredReservations.Length;
    });
    }

    public bool TryConsumeInitializedPollAfterCloseRequest(string invoiceId, Guid? sessionInstanceId = null) => WriteWithConcurrencyRetry(context =>
    {
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        CheckInstance(sessionData, ref sessionInstanceId);
        if (sessionData is null ||
            !sessionData.IsCloseRequested ||
            sessionData.InitializedPollAfterCloseRequestConsumed)
        {
            return false;
        }

        sessionData.InitializedPollAfterCloseRequestConsumed = true;
        sessionData.UpdatedAt = DateTimeOffset.UtcNow;
        context.SaveChanges();
        return true;
    });

    internal DatabaseBackedReceiverPersister CreatePersister(PayjoinReceiverSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new DatabaseBackedReceiverPersister(this, session.InvoiceId, session.SessionInstanceId, session.AttemptVersion);
    }

    internal void AppendEvents(DatabaseBackedReceiverPersister owner, IReadOnlyList<string> events) => CommitProtocolWrite(owner, events, null);

    internal void AttachOriginalPayment(DatabaseBackedReceiverPersister owner, PayjoinReceiverPaymentValidation.OriginalPayment payment) =>
        CommitProtocolWrite(owner, [], bridge =>
        {
            if (bridge.Status != PayjoinAccountingBridgeStatus.PendingFallback) throw new PayjoinSessionStaleException();
            bridge.FallbackTransactionId = payment.TransactionId;
            bridge.FallbackOutputIndex = payment.OutputIndex;
            bridge.FallbackValueSats = payment.AmountSats;
            bridge.EffectiveInvoiceValueSats = payment.AmountSats;
            bridge.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
        });

    internal void CommitReceiverOutputs(DatabaseBackedReceiverPersister owner, IReadOnlyList<string> events,
        PayjoinReceiverOutputBuilder.OutputReplacement outputs) => CommitProtocolWrite(owner, events, bridge =>
        {
            RequirePendingBridge(bridge);
            var script = Convert.ToHexString(outputs.SettlementScript);
            var amount = checked((long)outputs.SettlementAmountSats);
            if (bridge.EffectiveInvoiceValueSats != amount || bridge.ExpectedFinalTransactionId is not null ||
                (bridge.SettlementScript is not null && bridge.SettlementScript != script))
                throw new PayjoinAccountingReconciliationDataException("Conflicting committed receiver outputs.");
            bridge.SettlementScript = script;
            bridge.SettlementKeyPath = outputs.SettlementKeyPath?.ToString();
            bridge.EffectiveInvoiceValueSats = amount;
        });

    internal sealed record ProposalSettlement(string TransactionId, long OutputIndex, long ValueSats);

    internal void CommitProposal(DatabaseBackedReceiverPersister owner, IReadOnlyList<string> events, ProposalSettlement settlement) =>
        CommitProtocolWrite(owner, events, bridge => RecordProposalExpectation(bridge, settlement));

    internal void BackfillProposalExpectation(DatabaseBackedReceiverPersister owner, ProposalSettlement settlement) =>
        CommitProtocolWrite(owner, [], bridge => RecordProposalExpectation(bridge, settlement));

    private static void RecordProposalExpectation(PayjoinAccountingBridgeData bridge, ProposalSettlement settlement)
    {
        RequirePendingBridge(bridge);
        bridge.ExpectedFinalTransactionId = settlement.TransactionId;
        bridge.ExpectedFinalOutputIndex = settlement.OutputIndex;
        bridge.ExpectedFinalValueSats = settlement.ValueSats;
        bridge.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
    }

    private static void RequirePendingBridge(PayjoinAccountingBridgeData bridge)
    {
        if (bridge.Status is not (PayjoinAccountingBridgeStatus.PendingFallback or PayjoinAccountingBridgeStatus.PendingFinalTransaction))
            throw new PayjoinSessionStaleException();
    }

    internal void FailPayment(DatabaseBackedReceiverPersister owner, string reason) => CommitProtocolWrite(owner, [], bridge =>
    {
        bridge.Status = PayjoinAccountingBridgeStatus.Failed;
        bridge.FailureMessage = reason;
    });

    /// <summary>
    /// Appends session events and applies an accounting-bridge update in one database transaction, so a
    /// restart cannot observe the session state advanced while the accounting record lags behind.
    /// </summary>
    private void CommitProtocolWrite(DatabaseBackedReceiverPersister owner, IReadOnlyList<string> events, Action<PayjoinAccountingBridgeData>? updateBridge)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var invoiceId = owner.InvoiceId;
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0 && updateBridge is null)
        {
            return;
        }

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var context = _pluginDbContextFactory.CreateContext();
            var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
            if (sessionData is null) throw new PayjoinSessionStaleException();
            CheckOwner(owner, sessionData);
            var createdAt = DateTimeOffset.UtcNow;
            var lastSequence = context.ReceiverSessionEvents
                .Where(x => x.InvoiceId == invoiceId)
                .Select(x => (int?)x.Sequence)
                .Max() ?? 0;

            // TODO: Revisit receiver-session event retention before the log grows without bound.
            sessionData.UpdatedAt = createdAt;
            sessionData.AttemptVersion = checked(sessionData.AttemptVersion + 1);
            foreach (var @event in events)
            {
                lastSequence = checked(lastSequence + 1);
                context.ReceiverSessionEvents.Add(new PayjoinReceiverSessionEventData
                {
                    InvoiceId = invoiceId,
                    Sequence = lastSequence,
                    Event = @event,
                    CreatedAt = createdAt
                });
            }

            if (updateBridge is not null)
            {
                var bridge = context.AccountingBridges.SingleOrDefault(x => x.InvoiceId == invoiceId);
                if (bridge is null) throw new PayjoinAccountingReconciliationDataException("Missing accounting bridge for receiver transition.");
                updateBridge(bridge);
                bridge.UpdatedAt = PayjoinAccountingBridgeService.NextWriteTime(bridge, createdAt);
            }

            try
            {
                context.SaveChanges();
                ConfirmWrite(owner, sessionData);
                return;
            }
            catch (DbUpdateException ex) when (IsReceiverSessionEventSequenceConflict(ex) || ex is DbUpdateConcurrencyException)
            {
                if (attempt == maxAttempts)
                {
                    throw;
                }

                // A concurrent app instance may have claimed the next sequence first.
                // The unique (InvoiceId, Sequence) index is the durable ordering guard.
            }
        }
    }

    private bool IsReceiverSessionEventSequenceConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.ReceiverSessionEventsInvoiceSequenceIndex);
    }

    private bool IsReceiverSessionConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.ReceiverSessionsPrimaryKey);
    }

    private bool IsReceiverInputReservationConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.ReceiverInputReservationsOutPointIndex);
    }

    private static PayjoinReceiverSessionState CreateState(PayjoinReceiverSessionData sessionData, string[]? events = null)
    {
        return new PayjoinReceiverSessionState(
            sessionData.InvoiceId,
            sessionData.StoreId,
            sessionData.ReceiverAddress,
            sessionData.MonitoringExpiresAt,
            sessionData.CreatedAt,
            sessionData.UpdatedAt,
            sessionData.IsCloseRequested,
            sessionData.CloseInvoiceStatus,
            sessionData.CloseRequestedAt,
            sessionData.InitializedPollAfterCloseRequestConsumed,
            sessionData.ContributedInputTransactionId,
            sessionData.ContributedInputOutputIndex,
            events ?? [],
            sessionData.PayjoinUri,
            sessionData.SessionInstanceId,
            sessionData.AttemptVersion);
    }

    private static bool MustRetainHistoryForAccounting(PayjoinPluginDbContext context, string invoiceId) =>
        context.AccountingBridges.Any(x => x.InvoiceId == invoiceId &&
            (x.Status == PayjoinAccountingBridgeStatus.Failed ||
             (x.ExpectedFinalTransactionId != null && x.Status != PayjoinAccountingBridgeStatus.Reconciled)));

    private static IReadOnlyCollection<PayjoinReceiverSessionState> LoadPollableSessions(PayjoinPluginDbContext context)
    {
        using var snapshot = context.Database.IsRelational()
            ? context.Database.BeginTransaction(context.Database.IsNpgsql() ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable) : null;
        var sessionData = context.ReceiverSessions
            .AsNoTracking()
            .Where(x => !context.AccountingBridges.Any(b => b.InvoiceId == x.InvoiceId &&
                (b.Status == PayjoinAccountingBridgeStatus.Failed ||
                 (b.Status == PayjoinAccountingBridgeStatus.Expired && b.ExpectedFinalTransactionId != null && b.ExpectedFinalTransactionId != ""))))
            .OrderBy(x => x.CreatedAt)
            .ToArray();
        if (sessionData.Length == 0)
        {
            return [];
        }

        var invoiceIds = sessionData.Select(x => x.InvoiceId).ToArray();
        var sessionEvents = context.ReceiverSessionEvents
            .AsNoTracking()
            .Where(x => invoiceIds.Contains(x.InvoiceId))
            .OrderBy(x => x.InvoiceId)
            .ThenBy(x => x.Sequence)
            .ToArray()
            .GroupBy(x => x.InvoiceId)
            .ToDictionary(x => x.Key, x => x.Select(e => e.Event).ToArray());

        return sessionData
            .Select(row => CreateState(row, sessionEvents.GetValueOrDefault(row.InvoiceId)))
            .ToArray();
    }

    private static bool TryLoadSessionCore(
        PayjoinPluginDbContext context,
        string invoiceId,
        out PayjoinReceiverSessionState session)
    {
        var sessionData = LoadSessionWithEvents(context, invoiceId);
        if (sessionData is null)
        {
            session = null!;
            return false;
        }

        session = CreateState(sessionData, OrderedEvents(sessionData));
        return true;
    }

    private static PayjoinReceiverSessionData? LoadSessionWithEvents(PayjoinPluginDbContext context, string invoiceId) =>
        context.ReceiverSessions.Include(x => x.Events).AsSingleQuery().SingleOrDefault(x => x.InvoiceId == invoiceId);

    private static string[] OrderedEvents(PayjoinReceiverSessionData session) => session.Events.OrderBy(x => x.Sequence).Select(x => x.Event).ToArray();

    private static PayjoinReceiverSessionData? LoadSessionDataCore(
        PayjoinPluginDbContext context,
        string invoiceId,
        bool asNoTracking)
    {
        IQueryable<PayjoinReceiverSessionData> query = context.ReceiverSessions;
        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return query.SingleOrDefault(x => x.InvoiceId == invoiceId);
    }

    // INVARIANT: the only place session events may be deleted. PayjoinUri infers "has events" from being
    // set, so it is cleared with them; any retention code must go through here.
    internal static void RemoveAllSessionEvents(PayjoinPluginDbContext context, PayjoinReceiverSessionData sessionData)
    {
        var sessionEvents = context.ReceiverSessionEvents
            .Where(x => x.InvoiceId == sessionData.InvoiceId)
            .ToArray();
        if (sessionEvents.Length > 0)
        {
            context.ReceiverSessionEvents.RemoveRange(sessionEvents);
        }

        sessionData.PayjoinUri = null;
        sessionData.AttemptVersion = checked(sessionData.AttemptVersion + 1);
    }

    private static void AddBootstrapEvents(PayjoinPluginDbContext context, string invoiceId, IEnumerable<string> bootstrapEvents, DateTimeOffset createdAt)
    {
        var sequence = 0;
        foreach (var @event in bootstrapEvents)
        {
            sequence++;
            context.ReceiverSessionEvents.Add(new PayjoinReceiverSessionEventData
            {
                InvoiceId = invoiceId,
                Sequence = sequence,
                Event = @event,
                CreatedAt = createdAt
            });
        }
    }

    internal sealed class DatabaseBackedReceiverPersister : JsonReceiverSessionPersister
    {
        private readonly PayjoinReceiverSessionStore _store;
        internal string InvoiceId { get; }
        internal Guid SessionInstanceId { get; }
        internal int AttemptVersion { get; private set; }

        internal void ConfirmCommittedVersion(int version) => AttemptVersion = version;

        public DatabaseBackedReceiverPersister(PayjoinReceiverSessionStore store, string invoiceId, Guid sessionInstanceId, int attemptVersion)
        {
            _store = store;
            InvoiceId = invoiceId;
            SessionInstanceId = sessionInstanceId;
            AttemptVersion = attemptVersion;
        }

        public void Save(string @event)
        {
            _store.AppendEvents(this, [@event]);
        }

        public string[] Load()
        {
            var snapshot = _store.GetAttempt(InvoiceId, SessionInstanceId);
            if (snapshot.AttemptVersion != AttemptVersion) throw new PayjoinSessionStaleException();
            return snapshot.GetEvents();
        }

        void JsonReceiverSessionPersister.Save(string @event)
        {
            try { Save(@event); }
            catch (PayjoinSessionStaleException ex) { throw new ForeignException.InternalException(ex.Message); }
        }

        string[] JsonReceiverSessionPersister.Load()
        {
            try { return Load(); }
            catch (PayjoinSessionStaleException ex) { throw new ForeignException.InternalException(ex.Message); }
        }

        public void Close()
        {
        }
    }

    internal void RequireNegotiationAllowed(DatabaseBackedReceiverPersister owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        using var context = _pluginDbContextFactory.CreateContext();
        var row = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == owner.InvoiceId);
        if (row is null) throw new PayjoinSessionStaleException();
        CheckOwner(owner, row);
        if (!context.AccountingBridges.Any(x => x.InvoiceId == row.InvoiceId &&
            (x.Status == PayjoinAccountingBridgeStatus.PendingFallback || x.Status == PayjoinAccountingBridgeStatus.PendingFinalTransaction)))
            throw new PayjoinSessionStaleException("Negotiation is no longer pending.");
    }

    internal static void CheckOwner(DatabaseBackedReceiverPersister owner, PayjoinReceiverSessionData row)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (row.SessionInstanceId != owner.SessionInstanceId || row.AttemptVersion != owner.AttemptVersion)
            throw new PayjoinSessionStaleException();
    }

    internal static void ConfirmWrite(DatabaseBackedReceiverPersister owner, PayjoinReceiverSessionData row) =>
        owner.ConfirmCommittedVersion(row.AttemptVersion);

    private sealed class SnapshotPersister(string[] events) : JsonReceiverSessionPersister
    {
        public string[] Load() => events;
        public void Save(string @event) => throw new NotSupportedException();
        public void Close() { }
    }
}
