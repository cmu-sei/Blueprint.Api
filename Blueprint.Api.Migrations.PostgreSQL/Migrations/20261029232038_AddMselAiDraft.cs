using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddMselAiDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_draft",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_draft_stage",
                table: "msels",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ai_draft",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "ai_draft_stage",
                table: "msels");
        }
    }
}
