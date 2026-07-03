using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class Onda5Crm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MotivoDescarte",
                table: "SinergiasComprador",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProximaAcaoEm",
                table: "SinergiasComprador",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProximaAcaoNota",
                table: "SinergiasComprador",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InteracoesMatch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SinergiaId = table.Column<int>(type: "integer", nullable: false),
                    Autor = table.Column<string>(type: "text", nullable: false),
                    Texto = table.Column<string>(type: "text", nullable: false),
                    Em = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InteracoesMatch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InteracoesMatch_SinergiasComprador_SinergiaId",
                        column: x => x.SinergiaId,
                        principalTable: "SinergiasComprador",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InteracoesMatch_SinergiaId",
                table: "InteracoesMatch",
                column: "SinergiaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InteracoesMatch");

            migrationBuilder.DropColumn(
                name: "MotivoDescarte",
                table: "SinergiasComprador");

            migrationBuilder.DropColumn(
                name: "ProximaAcaoEm",
                table: "SinergiasComprador");

            migrationBuilder.DropColumn(
                name: "ProximaAcaoNota",
                table: "SinergiasComprador");
        }
    }
}
