using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationRolesToUserMselRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cite_evaluation_role",
                table: "user_msel_roles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gallery_exhibit_role",
                table: "user_msel_roles",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cite_evaluation_role",
                table: "user_msel_roles");

            migrationBuilder.DropColumn(
                name: "gallery_exhibit_role",
                table: "user_msel_roles");
        }
    }
}
