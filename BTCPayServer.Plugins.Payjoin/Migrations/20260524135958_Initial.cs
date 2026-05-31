using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.Payjoin");

            migrationBuilder.CreateTable(
                name: "ReceiverSessions",
                schema: "BTCPayServer.Plugins.Payjoin",
                columns: table => new
                {
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    ReceiverAddress = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OhttpRelayUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    MonitoringExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsCloseRequested = table.Column<bool>(type: "boolean", nullable: false),
                    CloseInvoiceStatus = table.Column<int>(type: "integer", nullable: true),
                    CloseRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InitializedPollAfterCloseRequestConsumed = table.Column<bool>(type: "boolean", nullable: false),
                    ContributedInputTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ContributedInputOutputIndex = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiverSessions", x => x.InvoiceId);
                });

            migrationBuilder.CreateTable(
                name: "ReceiverInputReservations",
                schema: "BTCPayServer.Plugins.Payjoin",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OutputIndex = table.Column<long>(type: "bigint", nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiverInputReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceiverInputReservations_ReceiverSessions_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "BTCPayServer.Plugins.Payjoin",
                        principalTable: "ReceiverSessions",
                        principalColumn: "InvoiceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReceiverSessionEvents",
                schema: "BTCPayServer.Plugins.Payjoin",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Event = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiverSessionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceiverSessionEvents_ReceiverSessions_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "BTCPayServer.Plugins.Payjoin",
                        principalTable: "ReceiverSessions",
                        principalColumn: "InvoiceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiverInputReservations_ExpiresAt",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverInputReservations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiverInputReservations_InvoiceId",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverInputReservations",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiverInputReservations_StoreId",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverInputReservations",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiverInputReservations_TransactionId_OutputIndex",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverInputReservations",
                columns: new[] { "TransactionId", "OutputIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiverSessionEvents_InvoiceId_Sequence",
                schema: "BTCPayServer.Plugins.Payjoin",
                table: "ReceiverSessionEvents",
                columns: new[] { "InvoiceId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "ReceiverInputReservations",
                schema: "BTCPayServer.Plugins.Payjoin");

            migrationBuilder.DropTable(
                name: "ReceiverSessionEvents",
                schema: "BTCPayServer.Plugins.Payjoin");

            migrationBuilder.DropTable(
                name: "ReceiverSessions",
                schema: "BTCPayServer.Plugins.Payjoin");
        }
    }
}
