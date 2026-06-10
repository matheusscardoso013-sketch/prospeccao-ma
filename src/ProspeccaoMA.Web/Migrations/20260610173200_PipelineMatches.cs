using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class PipelineMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Anotacoes",
                table: "SinergiasComprador",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AtualizadoEm",
                table: "SinergiasComprador",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "SinergiasComprador",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Anotacoes",
                table: "SinergiasComprador");

            migrationBuilder.DropColumn(
                name: "AtualizadoEm",
                table: "SinergiasComprador");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "SinergiasComprador");
        }
    }
}
