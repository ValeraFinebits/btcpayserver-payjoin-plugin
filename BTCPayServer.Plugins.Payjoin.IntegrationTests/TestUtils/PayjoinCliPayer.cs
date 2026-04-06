using NBitcoin;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class PayjoinCliPayer : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TransactionDetectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TransactionDetectionPollInterval = TimeSpan.FromMilliseconds(250);
    private const string DefaultFeeRateSatsPerVByte = "1";

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

    public async Task<PayjoinCliPaymentResult> PayAsync(Uri paymentUrl, Uri directoryUrl, Uri ohttpRelayUrl, Script expectedInvoiceScript, Money expectedInvoiceAmount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paymentUrl);
        ArgumentNullException.ThrowIfNull(directoryUrl);
        ArgumentNullException.ThrowIfNull(ohttpRelayUrl);
        ArgumentNullException.ThrowIfNull(expectedInvoiceScript);

        var knownTransactionIds = await GetWalletTransactionIdsAsync(cancellationToken).ConfigureAwait(false);
        await WriteConfigAsync(directoryUrl, ohttpRelayUrl, cancellationToken).ConfigureAwait(false);

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

        startInfo.ArgumentList.Add("send");
        startInfo.ArgumentList.Add(paymentUrl.OriginalString);
        startInfo.ArgumentList.Add("--fee-rate");
        startInfo.ArgumentList.Add(DefaultFeeRateSatsPerVByte);

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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProcessTimeout);

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
                $"payjoin-cli timed out after {ProcessTimeout.TotalSeconds:0} seconds.",
                process,
                stdout,
                stderr), ex);
        }
        catch (OperationCanceledException ex)
        {
            TryKill(process);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException(CreateFailureMessage(
                "payjoin-cli was canceled by the parent test token.",
                process,
                stdout,
                stderr), ex);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdoutText = await stdoutTask.ConfigureAwait(false);
        var stderrText = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(CreateFailureMessage(
                $"payjoin-cli exited with code {process.ExitCode}.",
                process,
                stdoutText,
                stderrText));
        }

        var transactionId = await GetNewTransactionIdAsync(
            knownTransactionIds,
            expectedInvoiceScript,
            expectedInvoiceAmount,
            process,
            stdoutText,
            stderrText,
            cancellationToken).ConfigureAwait(false);

        return new PayjoinCliPaymentResult(transactionId, stdoutText, stderrText);
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

    private async Task WriteConfigAsync(Uri directoryUrl, Uri ohttpRelayUrl, CancellationToken cancellationToken)
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
        configBuilder.Append("pj_directory = \"").Append(directoryUrl.AbsoluteUri).AppendLine("\"");
        configBuilder.Append("ohttp_relays = [\"").Append(ohttpRelayUrl.AbsoluteUri).AppendLine("\"]");

        var config = configBuilder.ToString();

        await File.WriteAllTextAsync(configPath, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetNewTransactionIdAsync(
        HashSet<string> knownTransactionIds,
        Script expectedInvoiceScript,
        Money expectedInvoiceAmount,
        Process process,
        string stdout,
        string stderr,
        CancellationToken cancellationToken)
    {
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

                matchingTransactionIds = await GetMatchingTransactionIdsAsync(candidateTransactionIds, expectedInvoiceScript, expectedInvoiceAmount, ct).ConfigureAwait(false);

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
                $"payjoin-cli completed successfully but the dedicated sender wallet did not expose exactly one invoice-matching transaction within {TransactionDetectionTimeout.TotalSeconds:0} seconds. CandidateCount={candidateTransactionIds.Length}, Candidates='{string.Join(",", candidateTransactionIds)}', MatchingCount={matchingTransactionIds.Length}, MatchingCandidates='{string.Join(",", matchingTransactionIds)}', ExpectedInvoiceScript='{expectedInvoiceScript}', ExpectedInvoiceAmountSats={expectedInvoiceAmount.Satoshi}, LastTransientError='{BitcoindNode.DescribeException(lastException)}'.",
                process,
                stdout,
                stderr),
            cancellationToken).ConfigureAwait(false);

        return detectedTransactionId!;
    }

    private async Task<string[]> GetMatchingTransactionIdsAsync(
        string[] candidateTransactionIds,
        Script expectedInvoiceScript,
        Money expectedInvoiceAmount,
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
            var hasExpectedInvoiceOutput = transaction.Outputs.Any(output =>
                output.ScriptPubKey == expectedInvoiceScript &&
                output.Value == expectedInvoiceAmount);

            if (hasExpectedInvoiceOutput)
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

    private static string CreateFailureMessage(string reason, Process process, string stdout, string stderr)
    {
        return $"{reason} Executable='{process.StartInfo.FileName}', WorkingDirectory='{process.StartInfo.WorkingDirectory}', DbPath='{Path.Combine(process.StartInfo.WorkingDirectory, "payjoin.sqlite")}', Stdout='{EscapeMultiline(stdout)}', Stderr='{EscapeMultiline(stderr)}'";
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
}

internal sealed record PayjoinCliPaymentResult(string TransactionId, string StandardOutput, string StandardError);
