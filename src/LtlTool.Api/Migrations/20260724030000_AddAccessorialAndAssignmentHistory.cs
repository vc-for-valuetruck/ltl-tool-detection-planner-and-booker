using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessorialAndAssignmentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessorialRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LoadId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LoadNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TripId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessorialRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoadAssignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LoadId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LoadNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TripId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CarrierId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CarrierName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Driver1Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Driver1Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Driver2Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Driver2Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OwnerOperatorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OwnerOperatorName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TruckId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TrailerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DispatcherId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DispatchedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CarrierAssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoadAssignments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessorialRecords_LoadId_TripId_EntityType_Type_Description_Amount",
                table: "AccessorialRecords",
                columns: new[] { "LoadId", "TripId", "EntityType", "Type", "Description", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessorialRecords_LastSeenAt",
                table: "AccessorialRecords",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_LoadAssignments_LoadId_CapturedAt",
                table: "LoadAssignments",
                columns: new[] { "LoadId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LoadAssignments_CapturedAt",
                table: "LoadAssignments",
                column: "CapturedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessorialRecords");

            migrationBuilder.DropTable(
                name: "LoadAssignments");
        }
    }
}
