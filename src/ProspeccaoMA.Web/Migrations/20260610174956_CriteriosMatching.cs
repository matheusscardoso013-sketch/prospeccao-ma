using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class CriteriosMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Abrangencia",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cultura",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModeloNegocio",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cultura",
                table: "Compradores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Exclusoes",
                table: "Compradores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FaturamentoMaxAlvo",
                table: "Compradores",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FaturamentoMinAlvo",
                table: "Compradores",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GeografiaAlvo",
                table: "Compradores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MargemEbitdaMinima",
                table: "Compradores",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModeloNegocioAlvo",
                table: "Compradores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TipoOperacao",
                table: "Compradores",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Abrangencia",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Cultura",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "ModeloNegocio",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Cultura",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "Exclusoes",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "FaturamentoMaxAlvo",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "FaturamentoMinAlvo",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "GeografiaAlvo",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "MargemEbitdaMinima",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "ModeloNegocioAlvo",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "TipoOperacao",
                table: "Compradores");
        }
    }
}
