using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    /// <inheritdoc />
    public partial class UpgradeToV040AlphaRc3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<bool>(
                name: "PaymentExposed",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "SenderSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "BTCPayServer.Plugins.Payjoin"."SenderSessions" AS s
                SET "PaymentExposed" = TRUE
                WHERE s."OriginalTransactionHex" IS NOT NULL OR EXISTS (
                    SELECT 1 FROM "BTCPayServer.Plugins.Payjoin"."SenderSessionEvents" AS e
                    WHERE e."SenderSessionId" = s."SenderSessionId");
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionInstanceId",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessions",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<string>(
                name: "SettlementKeyPath",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "AccountingBridges",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "PaymentExposed",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "SenderSessions");

            migrationBuilder.DropColumn(
                name: "SessionInstanceId",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessions");

            migrationBuilder.DropColumn(
                name: "SettlementKeyPath",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "AccountingBridges");
        }
    }
}
