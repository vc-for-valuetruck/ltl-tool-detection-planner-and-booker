using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddYardScheduleIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YardEvents",
                columns: table => new
                {
                    DedupeKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AffectsSchedulerInput = table.Column<bool>(type: "bit", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceRecordType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceRecordId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    YardLocationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YardEvents", x => x.DedupeKey);
                });

            migrationBuilder.CreateTable(
                name: "YardScheduleInputs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceRecordType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceRecordId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    YardLocationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SchedulerEligible = table.Column<bool>(type: "bit", nullable: false),
                    Readiness = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Completeness = table.Column<double>(type: "float", nullable: false),
                    HoldState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DockCompleted = table.Column<bool>(type: "bit", nullable: false),
                    SecurityCleared = table.Column<bool>(type: "bit", nullable: false),
                    HasOpenException = table.Column<bool>(type: "bit", nullable: false),
                    LatestOccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LatestEventType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LatestEventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EventCount = table.Column<int>(type: "int", nullable: false),
                    TruckId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TrailerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DockId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    WeightLbs = table.Column<double>(type: "float", nullable: true),
                    LengthInches = table.Column<double>(type: "float", nullable: true),
                    WidthInches = table.Column<double>(type: "float", nullable: true),
                    HeightInches = table.Column<double>(type: "float", nullable: true),
                    PieceCount = table.Column<int>(type: "int", nullable: true),
                    OriginLocationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DestinationLocationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AppointmentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RelationshipType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ParentSourceRecordId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RelatedRecordIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YardScheduleInputs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YardEvents_Sequence",
                table: "YardEvents",
                column: "Sequence");

            migrationBuilder.CreateIndex(
                name: "IX_YardEvents_SourceSystem_SourceRecordType_SourceRecordId",
                table: "YardEvents",
                columns: new[] { "SourceSystem", "SourceRecordType", "SourceRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_YardScheduleInputs_HoldState",
                table: "YardScheduleInputs",
                column: "HoldState");

            migrationBuilder.CreateIndex(
                name: "IX_YardScheduleInputs_Readiness",
                table: "YardScheduleInputs",
                column: "Readiness");

            migrationBuilder.CreateIndex(
                name: "IX_YardScheduleInputs_UpdatedAt",
                table: "YardScheduleInputs",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_YardScheduleInputs_YardLocationId",
                table: "YardScheduleInputs",
                column: "YardLocationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YardEvents");

            migrationBuilder.DropTable(
                name: "YardScheduleInputs");
        }
    }
}
