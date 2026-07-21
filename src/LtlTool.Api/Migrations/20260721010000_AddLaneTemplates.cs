using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLaneTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LaneTemplates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CorridorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OriginLabel = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DestinationLabel = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CadenceDays = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaneTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LaneTemplates_CorridorCode",
                table: "LaneTemplates",
                column: "CorridorCode");

            migrationBuilder.CreateIndex(
                name: "IX_LaneTemplates_CustomerName",
                table: "LaneTemplates",
                column: "CustomerName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LaneTemplates");
        }
    }
}
