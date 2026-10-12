using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlementKeyPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

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
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "SettlementKeyPath",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "AccountingBridges");
        }
    }
}
