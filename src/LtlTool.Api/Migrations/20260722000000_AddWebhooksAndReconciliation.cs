using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhooksAndReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Post-write reconciliation columns on the existing operation outbox (billing-document uploads).
            migrationBuilder.AddColumn<string>(
                name: "ReconciliationState",
                table: "AlvysOperations",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "NotApplicable");

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationDetail",
                table: "AlvysOperations",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultReference",
                table: "AlvysOperations",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlvysWebhookEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: true),
                    LoadNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RawBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProcessingState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ProcessingError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlvysWebhookEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "LoadFreshness",
                columns: table => new
                {
                    LoadNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastEventType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LastEventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LastChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangeCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadFreshness", x => x.LoadNumber);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlvysWebhookEvents_LoadNumber",
                table: "AlvysWebhookEvents",
                column: "LoadNumber");

            migrationBuilder.CreateIndex(
                name: "IX_AlvysWebhookEvents_ReceivedAt",
                table: "AlvysWebhookEvents",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlvysWebhookEvents");

            migrationBuilder.DropTable(
                name: "LoadFreshness");

            migrationBuilder.DropColumn(
                name: "ReconciliationState",
                table: "AlvysOperations");

            migrationBuilder.DropColumn(
                name: "ReconciliationDetail",
                table: "AlvysOperations");

            migrationBuilder.DropColumn(
                name: "ResultReference",
                table: "AlvysOperations");
        }
    }
}
