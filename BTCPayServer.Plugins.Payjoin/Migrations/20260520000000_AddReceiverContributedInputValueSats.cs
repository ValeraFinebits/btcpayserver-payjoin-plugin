using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System;
using BTCPayServer.Plugins.Payjoin.Data;

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    [DbContext(typeof(PayjoinPluginDbContext))]
    [Migration("20260520000000_AddReceiverContributedInputValueSats")]
    public partial class AddReceiverContributedInputValueSats : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<long>(
                name: "ContributedInputValueSats",
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverSessionsTable,
                type: "bigint",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "ContributedInputValueSats",
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverSessionsTable);
        }
    }
}
