using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddMselAssessorViewFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "show_group_on_assessor_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_integration_target_on_assessor_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_move_on_assessor_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_time_on_assessor_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "show_group_on_assessor_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_integration_target_on_assessor_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_move_on_assessor_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_time_on_assessor_view",
                table: "msels");
        }
    }
}
