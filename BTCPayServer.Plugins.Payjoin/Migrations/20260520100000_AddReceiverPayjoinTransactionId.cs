using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System;
using BTCPayServer.Plugins.Payjoin.Data;

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    [DbContext(typeof(PayjoinPluginDbContext))]
    [Migration("20260520100000_AddReceiverPayjoinTransactionId")]
    public partial class AddReceiverPayjoinTransactionId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "PayjoinTransactionId",
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverSessionsTable,
                type: "character varying(64)",
                maxLength: PayjoinPluginDbSchema.TransactionIdMaxLength,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "PayjoinTransactionId",
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverSessionsTable);
        }
    }
}
