using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class DockerRunner
{
    private readonly string _dockerExecutable;

    public DockerRunner(string dockerExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerExecutable);
        _dockerExecutable = dockerExecutable;
    }

    public async Task<ProcessExecutionResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(arguments);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the docker process.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("Failed to launch `docker`. Ensure Docker Desktop is installed and available on PATH.", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessExecutionResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    public ProcessExecutionResult Run(IReadOnlyList<string> arguments)
    {
        var startInfo = CreateStartInfo(arguments);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the docker process.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("Failed to launch `docker`. Ensure Docker Desktop is installed and available on PATH.", ex);
        }

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessExecutionResult(process.ExitCode, standardOutput, standardError);
    }

    public async Task<int> GetPublishedPortAsync(string containerName, int containerPort, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var portResult = await RunAsync(["port", containerName, $"{containerPort}/tcp"], cancellationToken).ConfigureAwait(false);
        if (portResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to resolve the published RPC port for '{containerName}'. {FormatResult(portResult)}");
        }

        using var reader = new StringReader(portResult.StandardOutput);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParsePublishedPort(line, out var publishedPort))
            {
                return publishedPort;
            }
        }

        throw new InvalidOperationException($"Docker did not report a published RPC port for '{containerName}'. {FormatResult(portResult)}");
    }

    public async Task<string> GetContainerDiagnosticsAsync(string containerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var portResult = await TryRunAsync(["port", containerName], cancellationToken).ConfigureAwait(false);
        var logsResult = await TryRunAsync(["logs", "--tail", "50", containerName], cancellationToken).ConfigureAwait(false);
        return $"Container='{containerName}', PortInfo=({FormatResult(portResult)}), Logs=({FormatResult(logsResult)})";
    }

    public void TryRemoveContainerNoThrow(string containerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        try
        {
            var result = Run(["rm", "-f", containerName]);
            if (result.ExitCode != 0)
            {
                Debug.WriteLine($"Failed to remove Docker container '{containerName}'. {FormatResult(result)}");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Failed to remove Docker container '{containerName}'. {EscapeMultiline(ex.Message)}");
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _dockerExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task<ProcessExecutionResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new ProcessExecutionResult(-1, string.Empty, ex.Message);
        }
    }

    private static bool TryParsePublishedPort(string value, out int publishedPort)
    {
        publishedPort = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separatorIndex = value.LastIndexOf(':');
        if (separatorIndex < 0 || separatorIndex == value.Length - 1)
        {
            return false;
        }

        return int.TryParse(value[(separatorIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out publishedPort);
    }

    internal static string FormatResult(ProcessExecutionResult result)
    {
        return $"ExitCode={result.ExitCode}, Stdout='{EscapeMultiline(result.StandardOutput)}', Stderr='{EscapeMultiline(result.StandardError)}'";
    }

    internal static string EscapeMultiline(string value)
    {
        return value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }
}

internal sealed record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError);
