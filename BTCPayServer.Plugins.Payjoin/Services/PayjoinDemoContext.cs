using System;
using Microsoft.Extensions.Logging;
using uniffi.payjoin;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinDemoContext : IDisposable
{
    private static readonly Action<ILogger, Exception?> LogPayjoinTestServicesInitialized =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(PayjoinDemoContext)), "Payjoin test services initialized");

    private readonly ILogger<PayjoinDemoContext> _logger;
    private readonly object _sync = new();
    private TestServices? _services;

    public PayjoinDemoContext(ILogger<PayjoinDemoContext> logger)
    {
        _logger = logger;
    }

    public bool IsReady { get; private set; }
    public System.Uri? DirectoryUrl { get; private set; }
    public System.Uri? OhttpRelayUrl { get; private set; }
    public System.Uri? OhttpGatewayUrl { get; private set; }
    public ReadOnlyMemory<byte> Certificate { get; private set; }
    internal OhttpKeys? OhttpKeys { get; private set; }

    public void Initialize()
    {
        lock (_sync)
        {
            if (IsReady)
            {
                return;
            }

            PayjoinMethods.InitTracing();
            TestServices? services = null;
            try
            {
                services = TestServices.Initialize();
                services.WaitForServicesReady();

                DirectoryUrl = CreateUri(services.DirectoryUrl());
                OhttpRelayUrl = CreateUri(services.OhttpRelayUrl());
                OhttpGatewayUrl = CreateUri(services.OhttpGatewayUrl());
                Certificate = services.Cert();
                OhttpKeys = services.FetchOhttpKeys();
                _services = services;
                services = null;
                IsReady = true;
                LogPayjoinTestServicesInitialized(_logger, null);
            }
            finally
            {
                services?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        OhttpKeys?.Dispose();
        _services?.Dispose();
    }

    private static System.Uri? CreateUri(string? value)
    {
        return System.Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }
}
