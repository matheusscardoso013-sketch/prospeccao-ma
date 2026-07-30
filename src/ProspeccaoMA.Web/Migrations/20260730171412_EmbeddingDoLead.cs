using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class EmbeddingDoLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextoEmbedding",
                table: "Leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TextoEmbeddingHash",
                table: "Leads",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TextoEmbedding",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "TextoEmbeddingHash",
                table: "Leads");
        }
    }
}
