using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddMselArticleMarking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "article_marking",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "article_marking_text",
                table: "msels",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "article_marking",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "article_marking_text",
                table: "msels");
        }
    }
}
