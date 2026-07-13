using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    /// <inheritdoc />
    public partial class RemoveReceiverSessionRelayUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "OhttpRelayUrl",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "OhttpRelayUrl",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessions",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");
        }
    }
}
