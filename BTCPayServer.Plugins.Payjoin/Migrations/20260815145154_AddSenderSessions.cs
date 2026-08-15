using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    /// <inheritdoc />
    public partial class AddSenderSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "SenderSessions",
                schema: "BTCPayServer.Plugins.Payjoin",
                columns: table => new
                {
                    SenderSessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    Bip21 = table.Column<string>(type: "text", nullable: false),
                    DestinationAddress = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AmountSats = table.Column<long>(type: "bigint", nullable: false),
                    OriginalTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PendingTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequestBaseUrl = table.Column<string>(type: "text", nullable: true),
                    BroadcastTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FailureMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderSessions", x => x.SenderSessionId);
                });

            migrationBuilder.CreateTable(
                name: "SenderSessionEvents",
                schema: "BTCPayServer.Plugins.Payjoin",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SenderSessionId = table.Column<string>(type: "character varying(64)", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Event = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderSessionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SenderSessionEvents_SenderSessions_SenderSessionId",
                        column: x => x.SenderSessionId,
                        principalSchema: "BTCPayServer.Plugins.Payjoin",
                        principalTable: "SenderSessions",
                        principalColumn: "SenderSessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SenderSessionEvents_SenderSessionId_Sequence",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "SenderSessionEvents",
                columns: new[] { "SenderSessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SenderSessions_OriginalTransactionId",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "SenderSessions",
                column: "OriginalTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_SenderSessions_PendingTransactionId",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "SenderSessions",
                column: "PendingTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_SenderSessions_Status_CreatedAt",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "SenderSessions",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SenderSessions_StoreId",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "SenderSessions",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "SenderSessionEvents",
                schema: "BTCPayServer.Plugins.Payjoin");

            migrationBuilder.DropTable(
                name: "SenderSessions",
                schema: "BTCPayServer.Plugins.Payjoin");
        }
    }
}
