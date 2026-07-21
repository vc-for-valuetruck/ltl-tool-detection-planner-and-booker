using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Signals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SignalType = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    EvidenceQuote = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SuggestedSurface = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    LoadNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IngestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Signals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Signals_LoadNumber",
                table: "Signals",
                column: "LoadNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Signals_Status",
                table: "Signals",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Signals");
        }
    }
}
