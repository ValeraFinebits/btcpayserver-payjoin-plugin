# BTCPay Server Async Payjoin Plugin (Alpha)

A [BTCPay Server](https://github.com/btcpayserver) plugin that adds [Async Payjoin (BIP 77)](https://github.com/bitcoin/bips/blob/master/bip-0077.mediawiki) support to the checkout flow. The plugin uses C# bindings to the Rust [Payjoin Dev Kit](https://github.com/payjoin/rust-payjoin) generated via UniFFI.

> [!WARNING]
> This plugin is in alpha and is intended for testing only. Please use a separate test environment. Do not use it in production.

## Testing the Alpha

The alpha is available from [BTCPay Plugin Builder](https://plugin-builder.btcpayserver.org/public/plugins/async-payjoin).

Feedback on setup and payments is welcome in [GitHub Issues](https://github.com/ValeraFinebits/btcpayserver-payjoin-plugin/issues). Please include your BTCPay Server and plugin versions when reporting a problem.

For security vulnerabilities, please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Rust 1.85+](https://rustup.rs/)
- Git

## Getting Started

### Cloning the Project

```sh
git clone --recurse-submodules https://github.com/ValeraFinebits/btcpayserver-payjoin-plugin
cd btcpayserver-payjoin-plugin
```

### Building

Generate the C# bindings from the Rust FFI crate, then build the .NET solution:

```sh
cd rust-payjoin/payjoin-ffi/csharp
bash ./scripts/generate_bindings.sh
cd ../../..

dotnet restore BTCPayServer.Plugins.Payjoin.sln
dotnet build BTCPayServer.Plugins.Payjoin.sln -c Release
```

### Running Tests

```sh
dotnet test BTCPayServer.Plugins.Payjoin.sln -c Release
```

### Managing EF Migrations

Entity Framework migrations for the plugin use the dedicated design-time startup project at `BTCPayServer.Plugins.Payjoin.Migrations`.

List migrations:

```sh
dotnet ef migrations list --project BTCPayServer.Plugins.Payjoin/BTCPayServer.Plugins.AsyncPayjoin.csproj --startup-project BTCPayServer.Plugins.Payjoin.Migrations/BTCPayServer.Plugins.Payjoin.Migrations.csproj
```

Add a migration:

```sh
dotnet ef migrations add <MigrationName> --project BTCPayServer.Plugins.Payjoin/BTCPayServer.Plugins.AsyncPayjoin.csproj --startup-project BTCPayServer.Plugins.Payjoin.Migrations/BTCPayServer.Plugins.Payjoin.Migrations.csproj --output-dir Migrations
```

Remove the latest migration:

```sh
dotnet ef migrations remove --project BTCPayServer.Plugins.Payjoin/BTCPayServer.Plugins.AsyncPayjoin.csproj --startup-project BTCPayServer.Plugins.Payjoin.Migrations/BTCPayServer.Plugins.Payjoin.Migrations.csproj --force
```

## Related Links

- Async Payjoin Rust implementation: https://github.com/payjoin/rust-payjoin
- BTCPay Server plugin development docs: https://docs.btcpayserver.org/Development/Plugins/

## Licence

[MIT](LICENSE) 
