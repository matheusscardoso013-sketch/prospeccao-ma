using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MapeamentoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class Fase4_JobExecucao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobExecucoes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NomeJob = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IniciadoEm = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConcluidoEm = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GatilhosCriados = table.Column<int>(type: "INTEGER", nullable: false),
                    AltaPrioridade = table.Column<int>(type: "INTEGER", nullable: false),
                    Sucesso = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErroMensagem = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobExecucoes", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobExecucoes");
        }
    }
}
