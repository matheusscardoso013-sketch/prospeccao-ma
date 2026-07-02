using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class DadoRico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PerfilSite",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PerfilSiteEm",
                table: "Leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CriteriosExtraidosEm",
                table: "Compradores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CriteriosValidados",
                table: "Compradores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PerfilSite",
                table: "Compradores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PerfilSiteEm",
                table: "Compradores",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PerfilSite",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "PerfilSiteEm",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CriteriosExtraidosEm",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "CriteriosValidados",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "PerfilSite",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "PerfilSiteEm",
                table: "Compradores");
        }
    }
}
