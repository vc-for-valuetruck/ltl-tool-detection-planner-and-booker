using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LtlTool.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CorridorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CustomerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CustomerName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ParentLoadId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ParentLoadNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    LoadsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EditHistoryJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InvoiceTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CombinedRevenue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CombinedDriverTripValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    DriverLoadedMiles = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CombinedRevenuePerMile = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    LoadsMissingBolCount = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinalizedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AlvysWriteback = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ParentLoadId",
                table: "Invoices",
                column: "ParentLoadId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Status",
                table: "Invoices",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Invoices");
        }
    }
}
