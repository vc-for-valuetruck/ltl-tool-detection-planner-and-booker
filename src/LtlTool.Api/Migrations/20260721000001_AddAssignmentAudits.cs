using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignmentAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssignmentAudits",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LoadId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DriverId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TruckId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TrailerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    MatchScore = table.Column<int>(type: "int", nullable: true),
                    MatchLabel = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ReasonType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OverrideReason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    WarningsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AlvysWriteback = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentAudits_LoadId",
                table: "AssignmentAudits",
                column: "LoadId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentAudits_RecordedBy",
                table: "AssignmentAudits",
                column: "RecordedBy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssignmentAudits");
        }
    }
}
