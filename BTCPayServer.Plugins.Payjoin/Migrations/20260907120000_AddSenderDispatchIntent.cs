using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Payjoin.Migrations;

[DbContext(typeof(PayjoinPluginDbContext))]
[Migration("20260907120000_AddSenderDispatchIntent")]
public sealed class AddSenderDispatchIntent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        System.ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.AddColumn<bool>(
            name: "PaymentExposed", schema: "BTCPayServer.Plugins.Payjoin",
            table: "SenderSessions", type: "boolean", nullable: false, defaultValue: false);
        // Older versions cannot prove that a signed original stayed private: the directory may
        // have accepted it without a saved response. Preserve those coins conservatively.
        migrationBuilder.Sql("""
            UPDATE "BTCPayServer.Plugins.Payjoin"."SenderSessions" AS s
            SET "PaymentExposed" = TRUE
            WHERE s."OriginalTransactionHex" IS NOT NULL OR EXISTS (
                SELECT 1 FROM "BTCPayServer.Plugins.Payjoin"."SenderSessionEvents" AS e
                WHERE e."SenderSessionId" = s."SenderSessionId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        System.ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.DropColumn(name: "PaymentExposed",
            schema: "BTCPayServer.Plugins.Payjoin", table: "SenderSessions");
    }
}
