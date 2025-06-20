using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class deliverymethodchanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "description",
                table: "scenario_events",
                newName: "integration_target");

            migrationBuilder.AddColumn<int>(
                name: "group_display_order",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "integration_target_display_order",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "move_display_order",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "show_integration_target_on_exercise_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_integration_target_on_scenario_event_list",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "time_display_order",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "group_display_order",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "integration_target_display_order",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "move_display_order",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_integration_target_on_exercise_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_integration_target_on_scenario_event_list",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "time_display_order",
                table: "msels");

            migrationBuilder.RenameColumn(
                name: "integration_target",
                table: "scenario_events",
                newName: "description");
        }
    }
}
