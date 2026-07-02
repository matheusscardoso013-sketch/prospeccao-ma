using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class SubscoresSinergia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScoreGeo",
                table: "SinergiasComprador",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScoreModelo",
                table: "SinergiasComprador",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScorePorte",
                table: "SinergiasComprador",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScoreSetor",
                table: "SinergiasComprador",
                type: "integer",
                nullable: true);

            // Limpeza única: pares fracos (score 1-39) que nunca foram trabalhados saem
            // da Mesa (viram Descartado com marca de descarte automático). Score 0 fica —
            // é falha da IA e a auto-cura reprocessa.
            migrationBuilder.Sql(
                "UPDATE \"SinergiasComprador\" " +
                "SET \"Status\" = 4, \"Anotacoes\" = 'descartado automaticamente (score baixo)' " +
                "WHERE \"Score\" > 0 AND \"Score\" < 40 AND \"Status\" = 0 AND \"Anotacoes\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScoreGeo",
                table: "SinergiasComprador");

            migrationBuilder.DropColumn(
                name: "ScoreModelo",
                table: "SinergiasComprador");

            migrationBuilder.DropColumn(
                name: "ScorePorte",
                table: "SinergiasComprador");

            migrationBuilder.DropColumn(
                name: "ScoreSetor",
                table: "SinergiasComprador");
        }
    }
}
