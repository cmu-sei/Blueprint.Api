using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddLmtMetadataFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "course_mode",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "educational_level",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "educational_use",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "keywords",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "msels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "msels",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "course_mode",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "educational_level",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "educational_use",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "keywords",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "language",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "msels");
        }
    }
}
