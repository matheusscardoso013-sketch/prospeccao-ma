using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class ModeloIANoPar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModeloIA",
                table: "SinergiasComprador",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModeloIA",
                table: "SinergiasComprador");
        }
    }
}
