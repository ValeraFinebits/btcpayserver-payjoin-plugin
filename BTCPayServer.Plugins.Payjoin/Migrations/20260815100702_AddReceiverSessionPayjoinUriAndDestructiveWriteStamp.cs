using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiverSessionPayjoinUriAndDestructiveWriteStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<int>(
                name: "DestructiveWriteStamp",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PayjoinUri",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "DestructiveWriteStamp",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessions");

            migrationBuilder.DropColumn(
                name: "PayjoinUri",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessions");
        }
    }
}
