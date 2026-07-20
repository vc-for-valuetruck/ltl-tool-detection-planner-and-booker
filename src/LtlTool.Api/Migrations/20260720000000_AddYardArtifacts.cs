using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddYardArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YardArtifacts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Yard = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TruckUnit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TrailerUnit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LoadNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SubmittedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PassedItems = table.Column<int>(type: "int", nullable: false),
                    FailedItems = table.Column<int>(type: "int", nullable: false),
                    NaItems = table.Column<int>(type: "int", nullable: false),
                    VerifiedPalletCount = table.Column<int>(type: "int", nullable: true),
                    VerifiedLengthInches = table.Column<int>(type: "int", nullable: true),
                    VerifiedWidthInches = table.Column<int>(type: "int", nullable: true),
                    VerifiedHeightInches = table.Column<int>(type: "int", nullable: true),
                    InspectionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FilesJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YardArtifacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YardArtifacts_LoadNumber",
                table: "YardArtifacts",
                column: "LoadNumber");

            migrationBuilder.CreateIndex(
                name: "IX_YardArtifacts_TrailerUnit",
                table: "YardArtifacts",
                column: "TrailerUnit");

            migrationBuilder.CreateIndex(
                name: "IX_YardArtifacts_TruckUnit",
                table: "YardArtifacts",
                column: "TruckUnit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YardArtifacts");
        }
    }
}
