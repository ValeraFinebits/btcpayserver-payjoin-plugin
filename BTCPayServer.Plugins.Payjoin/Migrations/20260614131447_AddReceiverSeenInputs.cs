using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiverSeenInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "ReceiverSeenInputs",
                schema: "BTCPayServer.Plugins.Payjoin",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiverSeenInputs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiverSeenInputs_TransactionId_OutputIndex",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSeenInputs",
                columns: new[] { "TransactionId", "OutputIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "ReceiverSeenInputs",
                schema: "BTCPayServer.Plugins.Payjoin");
        }
    }
}
