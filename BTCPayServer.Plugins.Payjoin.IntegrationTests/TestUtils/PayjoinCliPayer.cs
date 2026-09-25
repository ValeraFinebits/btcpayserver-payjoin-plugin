using NBitcoin;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class PayjoinCliPayer : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ResumeProcessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TransactionDetectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TransactionDetectionPollInterval = TimeSpan.FromMilliseconds(250);
    private const string DefaultFeeRateSatsPerVByte = "1";
    private const string PayjoinCliSentTransactionIdMarker = "Payjoin sent. TXID:";
    private const string PayjoinCliExpiredFallbackMarker = "Session expired. Broadcast the original transaction manually:";
    private const string PayjoinCliCancelledFallbackMarker = "Session cancelled. Broadcast the original transaction manually:";
    private const string PayjoinCliAlreadyCancelledFallbackMarker = "Session was already cancelled. Broadcast the original transaction manually:";
    private const string PayjoinCliFallbackTransactionMarker = "Broadcast the original transaction manually:";
    private const string PayjoinCliSessionEstablishedMarker = "Session established";
    private const string PayjoinCliNoSessionsToResumeMarker = "No sessions to resume.";
    private const string PayjoinCliCompletedSessionMarker = "Cannot cancel a completed session.";

    private readonly PayjoinCliSenderWallet _senderWallet;
    private readonly string _workingDirectory;
    private readonly string _databasePath;
    private readonly string _payjoinCliExecutablePath;

    public PayjoinCliPayer(PayjoinCliSenderWallet senderWallet)
    {
        _senderWallet = senderWallet ?? throw new ArgumentNullException(nameof(senderWallet));
        _workingDirectory = Path.Combine(Path.GetTempPath(), "btcpay-payjoin-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);
        _databasePath = Path.Combine(_workingDirectory, "payjoin.sqlite");
        _payjoinCliExecutablePath = ResolvePayjoinCliExecutablePath();
    }

    public async Task<PayjoinCliPaymentResult> PayAsync(Uri paymentUrl, IReadOnlyList<Uri> ohttpRelayUrls, Script expectedInvoiceScript, CancellationToken cancellationToken)
    {
        ValidateSendArguments(paymentUrl, ohttpRelayUrls, expectedInvoiceScript);

        var knownTransactionIds = await GetWalletTransactionIdsAsync(cancellationToken).ConfigureAwait(false);
        var commandResult = await RunSendAsync(paymentUrl, ohttpRelayUrls, cancellationToken).ConfigureAwait(false);
        var sessionId = TryParseSenderSessionId(commandResult.StandardOutput);

        var transactionId = await GetNewTransactionIdAsync(
            knownTransactionIds,
            expectedInvoiceScript,
            commandResult,
            cancellationToken).ConfigureAwait(false);

        return new PayjoinCliPaymentResult(
            transactionId,
            sessionId,
            commandResult.StandardOutput,
            commandResult.StandardError);
    }

    public async Task<PayjoinCliExpiryResult> PayExpectingExpiryAsync(
        Uri paymentUrl,
        IReadOnlyList<Uri> ohttpRelayUrls,
        Script expectedInvoiceScript,
        CancellationToken cancellationToken)
    {
        ValidateSendArguments(paymentUrl, ohttpRelayUrls, expectedInvoiceScript);

        var knownTransactionIds = await GetWalletTransactionIdsAsync(cancellationToken).ConfigureAwait(false);
        var commandResult = await RunSendAsync(
            paymentUrl,
            ohttpRelayUrls,
            cancellationToken).ConfigureAwait(false);
        var sessionId = GetRequiredSenderSessionId(commandResult);

        if (!commandResult.StandardOutput.Contains(PayjoinCliExpiredFallbackMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"payjoin-cli exited successfully without reporting '{PayjoinCliExpiredFallbackMarker}'.",
                commandResult));
        }

        var fallbackTransaction = TryParseFallbackTransaction(commandResult.StandardOutput);
        if (fallbackTransaction is null)
        {
            throw new InvalidOperationException(CreateFailureMessage(
                "payjoin-cli reported expiry without printing a parseable fallback transaction.",
                commandResult));
        }

        if (!fallbackTransaction.Outputs.Any(output => output.ScriptPubKey == expectedInvoiceScript))
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"The fallback transaction did not pay the expected invoice script '{expectedInvoiceScript}'.",
                commandResult));
        }

        await AssertNoNewWalletTransactionsAsync(
            knownTransactionIds,
            "payjoin-cli broadcast a transaction while handling expiry.",
            commandResult,
            cancellationToken).ConfigureAwait(false);

        return new PayjoinCliExpiryResult(
            fallbackTransaction,
            sessionId);
    }

    public async Task<PayjoinCliFailureResult> PayExpectingFailureAsync(
        Uri paymentUrl,
        IReadOnlyList<Uri> ohttpRelayUrls,
        Script expectedInvoiceScript,
        CancellationToken cancellationToken)
    {
        ValidateSendArguments(paymentUrl, ohttpRelayUrls, expectedInvoiceScript);

        var knownTransactionIds = await GetWalletTransactionIdsAsync(cancellationToken).ConfigureAwait(false);
        var commandResult = await RunSendAsync(
            paymentUrl,
            ohttpRelayUrls,
            cancellationToken,
            throwOnNonZeroExit: false).ConfigureAwait(false);

        if (commandResult.ExitCode == 0)
        {
            throw new InvalidOperationException(CreateFailureMessage(
                "payjoin-cli unexpectedly completed a send that was expected to fail.",
                commandResult));
        }

        var sessionId = TryParseSenderSessionId(commandResult.StandardOutput);

        await AssertNoNewWalletTransactionsAsync(
            knownTransactionIds,
            "payjoin-cli broadcast a transaction while handling an expected send failure.",
            commandResult,
            cancellationToken).ConfigureAwait(false);

        return new PayjoinCliFailureResult(
            sessionId,
            commandResult.StandardOutput,
            commandResult.StandardError);
    }

    public async Task<PayjoinCliCommandResult> GetHistoryAsync(
        IReadOnlyList<Uri> ohttpRelayUrls,
        CancellationToken cancellationToken)
    {
        ValidateOhttpRelayUrls(ohttpRelayUrls);
        var commandResult = await RunCommandAsync(
            ohttpRelayUrls,
            ["history"],
            throwOnNonZeroExit: true,
            processTimeout: null,
            cancellationToken).ConfigureAwait(false);
        return commandResult;
    }

    public async Task<PayjoinCliCommandResult> ResumeExpectingNoSessionsAsync(
        IReadOnlyList<Uri> ohttpRelayUrls,
        CancellationToken cancellationToken)
    {
        ValidateOhttpRelayUrls(ohttpRelayUrls);
        PayjoinCliCommandResult commandResult;
        try
        {
            commandResult = await RunCommandAsync(
                ohttpRelayUrls,
                ["resume"],
                throwOnNonZeroExit: true,
                processTimeout: ResumeProcessTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (
            !cancellationToken.IsCancellationRequested &&
            ex.InnerException is OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"payjoin-cli resume did not report '{PayjoinCliNoSessionsToResumeMarker}' within {ResumeProcessTimeout.TotalSeconds:0} seconds; an active sender session may remain. {ex.Message}",
                ex);
        }

        if (!commandResult.StandardOutput.Contains(PayjoinCliNoSessionsToResumeMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"payjoin-cli resume did not report '{PayjoinCliNoSessionsToResumeMarker}'.",
                commandResult));
        }

        return commandResult;
    }

    public async Task<Transaction> CancelExpiredSessionAgainWithoutBroadcastAsync(
        string sessionId,
        IReadOnlyList<Uri> ohttpRelayUrls,
        CancellationToken cancellationToken)
    {
        return await CancelSessionWithoutBroadcastExpectingFallbackAsync(
            sessionId,
            ohttpRelayUrls,
            PayjoinCliExpiredFallbackMarker,
            expectedInvoiceScript: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Transaction> CancelSessionWithoutBroadcastAsync(
        string sessionId,
        IReadOnlyList<Uri> ohttpRelayUrls,
        Script expectedInvoiceScript,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedInvoiceScript);
        return await CancelSessionWithoutBroadcastExpectingFallbackAsync(
            sessionId,
            ohttpRelayUrls,
            PayjoinCliCancelledFallbackMarker,
            expectedInvoiceScript,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Transaction> CancelAlreadyCancelledSessionWithoutBroadcastAsync(
        string sessionId,
        IReadOnlyList<Uri> ohttpRelayUrls,
        Script expectedInvoiceScript,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedInvoiceScript);
        return await CancelSessionWithoutBroadcastExpectingFallbackAsync(
            sessionId,
            ohttpRelayUrls,
            PayjoinCliAlreadyCancelledFallbackMarker,
            expectedInvoiceScript,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PayjoinCliCommandResult> CancelCompletedSessionWithoutBroadcastAsync(
        string sessionId,
        string transactionId,
        IReadOnlyList<Uri> ohttpRelayUrls,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ValidateOhttpRelayUrls(ohttpRelayUrls);

        var knownTransactionIds = await GetWalletTransactionIdsAsync(cancellationToken).ConfigureAwait(false);
        var commandResult = await RunCommandAsync(
            ohttpRelayUrls,
            ["cancel", sessionId, "--no-broadcast"],
            throwOnNonZeroExit: true,
            processTimeout: null,
            cancellationToken).ConfigureAwait(false);

        if (!commandResult.StandardOutput.Contains(PayjoinCliCompletedSessionMarker, StringComparison.Ordinal) ||
            !commandResult.StandardOutput.Contains(sessionId, StringComparison.Ordinal) ||
            !commandResult.StandardOutput.Contains(transactionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"payjoin-cli cancel did not identify completed sender session '{sessionId}' and transaction '{transactionId}'.",
                commandResult));
        }

        await AssertNoNewWalletTransactionsAsync(
            knownTransactionIds,
            "payjoin-cli broadcast a transaction while refusing cancellation of a completed session.",
            commandResult,
            cancellationToken).ConfigureAwait(false);

        return commandResult;
    }

    private async Task<Transaction> CancelSessionWithoutBroadcastExpectingFallbackAsync(
        string sessionId,
        IReadOnlyList<Uri> ohttpRelayUrls,
        string expectedMarker,
        Script? expectedInvoiceScript,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMarker);
        ValidateOhttpRelayUrls(ohttpRelayUrls);

        var knownTransactionIds = await GetWalletTransactionIdsAsync(cancellationToken).ConfigureAwait(false);
        var commandResult = await RunCommandAsync(
            ohttpRelayUrls,
            ["cancel", sessionId, "--no-broadcast"],
            throwOnNonZeroExit: true,
            processTimeout: null,
            cancellationToken).ConfigureAwait(false);

        if (!commandResult.StandardOutput.Contains(expectedMarker, StringComparison.Ordinal) ||
            !commandResult.StandardOutput.Contains(sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"payjoin-cli cancel did not report sender session '{sessionId}' with '{expectedMarker}'.",
                commandResult));
        }

        var fallbackTransaction = TryParseFallbackTransaction(commandResult.StandardOutput);
        if (fallbackTransaction is null)
        {
            throw new InvalidOperationException(CreateFailureMessage(
                "payjoin-cli cancel did not print the persisted fallback transaction.",
                commandResult));
        }

        if (expectedInvoiceScript is not null &&
            !fallbackTransaction.Outputs.Any(output => output.ScriptPubKey == expectedInvoiceScript))
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"The persisted fallback transaction did not pay the expected invoice script '{expectedInvoiceScript}'.",
                commandResult));
        }

        await AssertNoNewWalletTransactionsAsync(
            knownTransactionIds,
            "payjoin-cli broadcast a transaction while cancelling with --no-broadcast.",
            commandResult,
            cancellationToken).ConfigureAwait(false);

        return fallbackTransaction;
    }

    private async Task<PayjoinCliCommandResult> RunSendAsync(
        Uri paymentUrl,
        IReadOnlyList<Uri> ohttpRelayUrls,
        CancellationToken cancellationToken,
        bool throwOnNonZeroExit = true)
    {
        return await RunCommandAsync(
            ohttpRelayUrls,
            ["send", paymentUrl.OriginalString, "--fee-rate", DefaultFeeRateSatsPerVByte],
            throwOnNonZeroExit,
            processTimeout: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PayjoinCliCommandResult> RunCommandAsync(
        IReadOnlyList<Uri> ohttpRelayUrls,
        IReadOnlyList<string> arguments,
        bool throwOnNonZeroExit,
        TimeSpan? processTimeout,
        CancellationToken cancellationToken)
    {
        await WriteConfigAsync(ohttpRelayUrls, cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = _payjoinCliExecutablePath,
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["RUST_LOG"] = "debug";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the payjoin-cli process.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to launch payjoin-cli from '{_payjoinCliExecutablePath}'. Ensure the hardcoded executable path is correct.", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var effectiveProcessTimeout = processTimeout ?? ProcessTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveProcessTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException(CreateFailureMessage(
                $"payjoin-cli timed out after {effectiveProcessTimeout.TotalSeconds:0} seconds.",
                CreateCommandResult(process, stdout, stderr)), ex);
        }
        catch (OperationCanceledException ex)
        {
            TryKill(process);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException(CreateFailureMessage(
                "payjoin-cli was canceled by the parent test token.",
                CreateCommandResult(process, stdout, stderr)), ex);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdoutText = await stdoutTask.ConfigureAwait(false);
        var stderrText = await stderrTask.ConfigureAwait(false);
        var commandResult = CreateCommandResult(process, stdoutText, stderrText);

        if (throwOnNonZeroExit && process.ExitCode != 0)
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"payjoin-cli exited with code {process.ExitCode}.",
                commandResult));
        }

        return commandResult;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task WriteConfigAsync(IReadOnlyList<Uri> ohttpRelayUrls, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(_workingDirectory, "config.toml");
        var configBuilder = new StringBuilder();
        configBuilder.Append("db_path = \"").Append(ToTomlPath(_databasePath)).AppendLine("\"");
        configBuilder.AppendLine();
        configBuilder.AppendLine("[bitcoind]");
        configBuilder.Append("rpchost = \"").Append(_senderWallet.RpcHost.AbsoluteUri).AppendLine("\"");
        configBuilder.Append("rpcuser = \"").Append(EscapeTomlString(_senderWallet.RpcUser)).AppendLine("\"");
        configBuilder.Append("rpcpassword = \"").Append(EscapeTomlString(_senderWallet.RpcPassword)).AppendLine("\"");
        configBuilder.AppendLine();
        configBuilder.AppendLine("[v2]");
        configBuilder.Append("ohttp_relays = [");
        for (var i = 0; i < ohttpRelayUrls.Count; i++)
        {
            if (i > 0)
            {
                configBuilder.Append(", ");
            }

            configBuilder.Append('"').Append(ohttpRelayUrls[i].AbsoluteUri).Append('"');
        }

        configBuilder.AppendLine("]");

        var config = configBuilder.ToString();

        await File.WriteAllTextAsync(configPath, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetNewTransactionIdAsync(
        HashSet<string> knownTransactionIds,
        Script expectedInvoiceScript,
        PayjoinCliCommandResult commandResult,
        CancellationToken cancellationToken)
    {
        var stdoutTransactionId = TryParseSentTransactionIdFromStdout(commandResult.StandardOutput);
        if (!string.IsNullOrWhiteSpace(stdoutTransactionId))
        {
            await AsyncPolling.WaitUntilAsync(
                TransactionDetectionTimeout,
                TransactionDetectionPollInterval,
                async ct =>
                {
                    var currentTransactionIds = await GetWalletTransactionIdsAsync(ct).ConfigureAwait(false);
                    return currentTransactionIds.Contains(stdoutTransactionId);
                },
                BitcoindNode.IsTransientRpcException,
                lastException => CreateFailureMessage(
                    $"payjoin-cli reported TXID '{stdoutTransactionId}', but the dedicated sender wallet did not expose it within {TransactionDetectionTimeout.TotalSeconds:0} seconds. LastTransientError='{BitcoindNode.DescribeException(lastException)}'.",
                    commandResult),
                cancellationToken).ConfigureAwait(false);

            return stdoutTransactionId;
        }

        string[] candidateTransactionIds = [];
        string[] matchingTransactionIds = [];
        string? detectedTransactionId = null;

        await AsyncPolling.WaitUntilAsync(
            TransactionDetectionTimeout,
            TransactionDetectionPollInterval,
            async ct =>
            {
                var currentTransactionIds = await GetWalletTransactionIdsAsync(ct).ConfigureAwait(false);
                candidateTransactionIds = currentTransactionIds
                    .Where(txid => !knownTransactionIds.Contains(txid))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                matchingTransactionIds = await GetMatchingTransactionIdsAsync(candidateTransactionIds, expectedInvoiceScript, ct).ConfigureAwait(false);

                if (matchingTransactionIds.Length == 1)
                {
                    detectedTransactionId = matchingTransactionIds[0];
                    return true;
                }

                detectedTransactionId = null;
                return false;
            },
            BitcoindNode.IsTransientRpcException,
            lastException => CreateFailureMessage(
                $"payjoin-cli completed successfully but the dedicated sender wallet did not expose exactly one unified receiver-output transaction within {TransactionDetectionTimeout.TotalSeconds:0} seconds. CandidateCount={candidateTransactionIds.Length}, Candidates='{string.Join(",", candidateTransactionIds)}', MatchingCount={matchingTransactionIds.Length}, MatchingCandidates='{string.Join(",", matchingTransactionIds)}', ExpectedInvoiceScript='{expectedInvoiceScript}', LastTransientError='{BitcoindNode.DescribeException(lastException)}'.",
                commandResult),
            cancellationToken).ConfigureAwait(false);

        return detectedTransactionId!;
    }

    private static string? TryParseSentTransactionIdFromStdout(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } line)
        {
            var markerIndex = line.IndexOf(PayjoinCliSentTransactionIdMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var txid = line[(markerIndex + PayjoinCliSentTransactionIdMarker.Length)..].Trim();
            return IsValidTransactionId(txid) ? txid : null;
        }

        return null;
    }

    private static string GetRequiredSenderSessionId(PayjoinCliCommandResult commandResult)
    {
        var sessionId = TryParseSenderSessionId(commandResult.StandardOutput);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"payjoin-cli did not report a sender '{PayjoinCliSessionEstablishedMarker}' line.",
                commandResult));
        }

        return sessionId;
    }

    private static string? TryParseSenderSessionId(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains(PayjoinCliSessionEstablishedMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var prefixStart = line.IndexOf("[Sender", StringComparison.Ordinal);
            if (prefixStart < 0)
            {
                continue;
            }

            var prefixEnd = line.IndexOf(']', prefixStart + 1);
            if (prefixEnd < 0)
            {
                continue;
            }

            var prefixParts = line[(prefixStart + 1)..prefixEnd]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (prefixParts.Length == 2 && string.Equals(prefixParts[0], "Sender", StringComparison.Ordinal))
            {
                return prefixParts[1];
            }
        }

        return null;
    }

    private Transaction? TryParseFallbackTransaction(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        using var reader = new StringReader(stdout);
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains(PayjoinCliFallbackTransactionMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var transactionHex = reader.ReadLine();
            return transactionHex is not null && Transaction.TryParse(
                transactionHex.Trim(),
                _senderWallet.WalletRpcClient.Network,
                out var transaction)
                    ? transaction
                    : null;
        }

        return null;
    }

    private async Task AssertNoNewWalletTransactionsAsync(
        HashSet<string> knownTransactionIds,
        string reason,
        PayjoinCliCommandResult commandResult,
        CancellationToken cancellationToken)
    {
        var currentTransactionIds = await GetWalletTransactionIdsAsync(cancellationToken).ConfigureAwait(false);
        var unexpectedTransactionIds = currentTransactionIds
            .Where(transactionId => !knownTransactionIds.Contains(transactionId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unexpectedTransactionIds.Length > 0)
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"{reason} UnexpectedTransactions='{string.Join(",", unexpectedTransactionIds)}'.",
                commandResult));
        }
    }

    private static bool IsValidTransactionId(string? txid)
    {
        if (string.IsNullOrWhiteSpace(txid) || txid.Length != 64)
        {
            return false;
        }

        foreach (var ch in txid)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string[]> GetMatchingTransactionIdsAsync(
        string[] candidateTransactionIds,
        Script expectedInvoiceScript,
        CancellationToken cancellationToken)
    {
        if (candidateTransactionIds.Length == 0)
        {
            return [];
        }

        var matchingTransactionIds = new List<string>(candidateTransactionIds.Length);
        foreach (var candidateTransactionId in candidateTransactionIds)
        {
            var transaction = await GetWalletTransactionAsync(candidateTransactionId, cancellationToken).ConfigureAwait(false);
            var matchesUnifiedReceiverOutputModel = transaction.Inputs.Count > 1 &&
                                                  transaction.Outputs.Count == 2 &&
                                                  transaction.Outputs.All(output => output.ScriptPubKey != expectedInvoiceScript);

            if (matchesUnifiedReceiverOutputModel)
            {
                matchingTransactionIds.Add(candidateTransactionId);
            }
        }

        return matchingTransactionIds.ToArray();
    }

    private async Task<HashSet<string>> GetWalletTransactionIdsAsync(CancellationToken cancellationToken)
    {
        var response = await _senderWallet.WalletRpcClient
            .SendCommandAsync("listtransactions", "*", 1000, 0, true)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var transactions = response.Result as JArray ?? throw new InvalidOperationException("listtransactions returned no array result.");

        return transactions
            .OfType<JObject>()
            .Select(entry => entry.Value<string>("txid"))
            .Where(txid => !string.IsNullOrWhiteSpace(txid))
            .Select(txid => txid!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<Transaction> GetWalletTransactionAsync(string transactionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        var response = await _senderWallet.WalletRpcClient
            .SendCommandAsync("gettransaction", transactionId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var transaction = response.Result as JObject ?? throw new InvalidOperationException($"gettransaction returned no object result for txid '{transactionId}'.");
        var transactionHex = transaction.Value<string>("hex") ?? throw new InvalidOperationException($"gettransaction.hex was missing for txid '{transactionId}'.");
        return Transaction.Parse(transactionHex, _senderWallet.WalletRpcClient.Network);
    }

    private static string EscapeTomlString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string ToTomlPath(string path)
    {
        return path.Replace("\\", "/", StringComparison.Ordinal);
    }

    private static string EscapeMultiline(string value)
    {
        return value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string ResolvePayjoinCliExecutablePath()
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "rust-payjoin",
            "target",
            "debug",
            GetPayjoinCliExecutableName()));

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"The payjoin-cli executable path does not exist: '{fullPath}'. Build rust-payjoin/target/debug/{GetPayjoinCliExecutableName()} before running the integration test.");
        }

        return fullPath;
    }

    private static string GetPayjoinCliExecutableName()
    {
        return OperatingSystem.IsWindows() ? "payjoin-cli.exe" : "payjoin-cli";
    }

    private PayjoinCliCommandResult CreateCommandResult(Process process, string stdout, string stderr)
    {
        return new PayjoinCliCommandResult(
            process.HasExited ? process.ExitCode : null,
            process.StartInfo.FileName,
            process.StartInfo.WorkingDirectory,
            _databasePath,
            stdout,
            stderr);
    }

    private static string CreateFailureMessage(string reason, PayjoinCliCommandResult commandResult)
    {
        return $"{reason} Executable='{commandResult.ExecutablePath}', WorkingDirectory='{commandResult.WorkingDirectory}', DbPath='{commandResult.DatabasePath}', ExitCode='{commandResult.ExitCode}', Stdout='{EscapeMultiline(commandResult.StandardOutput)}', Stderr='{EscapeMultiline(commandResult.StandardError)}'";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void ValidateSendArguments(Uri paymentUrl, IReadOnlyList<Uri> ohttpRelayUrls, Script expectedInvoiceScript)
    {
        ArgumentNullException.ThrowIfNull(paymentUrl);
        ValidateOhttpRelayUrls(ohttpRelayUrls);
        ArgumentNullException.ThrowIfNull(expectedInvoiceScript);
    }

    private static void ValidateOhttpRelayUrls(IReadOnlyList<Uri> ohttpRelayUrls)
    {
        ArgumentNullException.ThrowIfNull(ohttpRelayUrls);
        if (ohttpRelayUrls.Count == 0)
        {
            throw new InvalidOperationException("At least one OHTTP relay URL is required for payjoin-cli.");
        }
    }
}

internal sealed record PayjoinCliPaymentResult(
    string TransactionId,
    string? SessionId,
    string StandardOutput,
    string StandardError);

internal sealed record PayjoinCliExpiryResult(
    Transaction FallbackTransaction,
    string SessionId);

internal sealed record PayjoinCliFailureResult(
    string? SessionId,
    string StandardOutput,
    string StandardError);

internal sealed record PayjoinCliCommandResult(
    int? ExitCode,
    string ExecutablePath,
    string WorkingDirectory,
    string DatabasePath,
    string StandardOutput,
    string StandardError);
