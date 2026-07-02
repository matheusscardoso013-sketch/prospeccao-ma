using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspeccaoMA.Web.Migrations
{
    /// <inheritdoc />
    public partial class Embeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TeseEmbedding",
                table: "Compradores",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeseEmbeddingHash",
                table: "Compradores",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeseEmbedding",
                table: "Compradores");

            migrationBuilder.DropColumn(
                name: "TeseEmbeddingHash",
                table: "Compradores");
        }
    }
}
