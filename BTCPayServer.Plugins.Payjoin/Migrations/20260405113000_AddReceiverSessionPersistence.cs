using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using System;
using BTCPayServer.Plugins.Payjoin.Data;

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    [DbContext(typeof(PayjoinPluginDbContext))]
    [Migration("20260405113000_AddReceiverSessionPersistence")]
    public partial class AddReceiverSessionPersistence : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: PayjoinPluginDbSchema.ReceiverSessionsTable,
                schema: PayjoinPluginDbSchema.SchemaName,
                columns: table => new
                {
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    ReceiverAddress = table.Column<string>(type: "character varying(128)", maxLength: PayjoinPluginDbSchema.ReceiverAddressMaxLength, nullable: false),
                    OhttpRelayUrl = table.Column<string>(type: "character varying(2048)", maxLength: PayjoinPluginDbSchema.OhttpRelayUrlMaxLength, nullable: false),
                    MonitoringExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsCloseRequested = table.Column<bool>(type: "boolean", nullable: false),
                    CloseInvoiceStatus = table.Column<int>(type: "integer", nullable: true),
                    CloseRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InitializedPollAfterCloseRequestConsumed = table.Column<bool>(type: "boolean", nullable: false),
                    ContributedInputTransactionId = table.Column<string>(type: "character varying(64)", maxLength: PayjoinPluginDbSchema.TransactionIdMaxLength, nullable: true),
                    ContributedInputOutputIndex = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiverSessions", x => x.InvoiceId);
                });

            migrationBuilder.CreateTable(
                name: PayjoinPluginDbSchema.ReceiverSessionEventsTable,
                schema: PayjoinPluginDbSchema.SchemaName,
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
                        principalSchema: PayjoinPluginDbSchema.SchemaName,
                        principalTable: PayjoinPluginDbSchema.ReceiverSessionsTable,
                        principalColumn: "InvoiceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: PayjoinPluginDbSchema.ReceiverSessionEventsInvoiceSequenceIndex,
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverSessionEventsTable,
                columns: new[] { "InvoiceId", "Sequence" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: PayjoinPluginDbSchema.ReceiverSessionEventsTable,
                schema: PayjoinPluginDbSchema.SchemaName);

            migrationBuilder.DropTable(
                name: PayjoinPluginDbSchema.ReceiverSessionsTable,
                schema: PayjoinPluginDbSchema.SchemaName);
        }
    }
}
