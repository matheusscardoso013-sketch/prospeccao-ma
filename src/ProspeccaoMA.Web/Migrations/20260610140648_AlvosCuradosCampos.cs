using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class AlvosCuradosCampos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MargemEbitda",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Responsavel",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Site",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValuationEstimado",
                table: "Leads",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MargemEbitda",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Responsavel",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Site",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ValuationEstimado",
                table: "Leads");
        }
    }
}
