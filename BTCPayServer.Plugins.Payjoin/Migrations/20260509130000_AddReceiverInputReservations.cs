using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using System;

namespace BTCPayServer.Plugins.Payjoin.Migrations
{
    [DbContext(typeof(PayjoinPluginDbContext))]
    [Migration("20260509130000_AddReceiverInputReservations")]
    public partial class AddReceiverInputReservations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: PayjoinPluginDbSchema.ReceiverInputReservationsTable,
                schema: PayjoinPluginDbSchema.SchemaName,
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(64)", maxLength: PayjoinPluginDbSchema.TransactionIdMaxLength, nullable: false),
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
                        principalSchema: PayjoinPluginDbSchema.SchemaName,
                        principalTable: PayjoinPluginDbSchema.ReceiverSessionsTable,
                        principalColumn: "InvoiceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: PayjoinPluginDbSchema.ReceiverInputReservationsExpiresAtIndex,
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverInputReservationsTable,
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: PayjoinPluginDbSchema.ReceiverInputReservationsInvoiceIdIndex,
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverInputReservationsTable,
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: PayjoinPluginDbSchema.ReceiverInputReservationsStoreIdIndex,
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverInputReservationsTable,
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: PayjoinPluginDbSchema.ReceiverInputReservationsOutPointIndex,
                schema: PayjoinPluginDbSchema.SchemaName,
                table: PayjoinPluginDbSchema.ReceiverInputReservationsTable,
                columns: new[] { "TransactionId", "OutputIndex" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: PayjoinPluginDbSchema.ReceiverInputReservationsTable,
                schema: PayjoinPluginDbSchema.SchemaName);
        }
    }
}
