using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddYardWebhooksAndReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YardWebhookEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    YardCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TractorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TrailerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DriverId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RawBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProcessingState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ProcessingError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YardWebhookEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "YardLtlOpportunities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DraftId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    YardCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ParentLoadId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SiblingLoadIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FreightJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedByStation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ScannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YardLtlOpportunities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YardWebhookEvents_EventType",
                table: "YardWebhookEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_YardWebhookEvents_ReceivedAt",
                table: "YardWebhookEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_YardLtlOpportunities_EventId",
                table: "YardLtlOpportunities",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YardLtlOpportunities_ReceivedAt",
                table: "YardLtlOpportunities",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YardLtlOpportunities");

            migrationBuilder.DropTable(
                name: "YardWebhookEvents");
        }
    }
}
