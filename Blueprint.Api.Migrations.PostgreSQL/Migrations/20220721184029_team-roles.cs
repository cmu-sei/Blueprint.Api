using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class teamroles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_msels_teams_team_id",
                table: "msels");

            migrationBuilder.DropForeignKey(
                name: "FK_user_msel_roles_scenario_events_scenario_event_id",
                table: "user_msel_roles");

            migrationBuilder.DropIndex(
                name: "IX_user_msel_roles_msel_id_scenario_event_id_user_id_role",
                table: "user_msel_roles");

            migrationBuilder.DropIndex(
                name: "IX_user_msel_roles_scenario_event_id",
                table: "user_msel_roles");

            migrationBuilder.DropIndex(
                name: "IX_msels_team_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "scenario_event_id",
                table: "user_msel_roles");

            migrationBuilder.DropColumn(
                name: "team_id",
                table: "msels");

            migrationBuilder.AddColumn<Guid>(
                name: "assigned_team_id",
                table: "scenario_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "msel_teams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_teams", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_teams_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_msel_teams_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_msel_roles_msel_id_user_id_role",
                table: "user_msel_roles",
                columns: new[] { "msel_id", "user_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scenario_events_assigned_team_id",
                table: "scenario_events",
                column: "assigned_team_id");

            migrationBuilder.CreateIndex(
                name: "IX_msel_teams_msel_id",
                table: "msel_teams",
                column: "msel_id");

            migrationBuilder.CreateIndex(
                name: "IX_msel_teams_team_id_msel_id",
                table: "msel_teams",
                columns: new[] { "team_id", "msel_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_scenario_events_teams_assigned_team_id",
                table: "scenario_events",
                column: "assigned_team_id",
                principalTable: "teams",
                principalColumn: "id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_scenario_events_teams_assigned_team_id",
                table: "scenario_events");

            migrationBuilder.DropTable(
                name: "msel_teams");

            migrationBuilder.DropIndex(
                name: "IX_user_msel_roles_msel_id_user_id_role",
                table: "user_msel_roles");

            migrationBuilder.DropIndex(
                name: "IX_scenario_events_assigned_team_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "assigned_team_id",
                table: "scenario_events");

            migrationBuilder.AddColumn<Guid>(
                name: "scenario_event_id",
                table: "user_msel_roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_msel_roles_msel_id_scenario_event_id_user_id_role",
                table: "user_msel_roles",
                columns: new[] { "msel_id", "scenario_event_id", "user_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_msel_roles_scenario_event_id",
                table: "user_msel_roles",
                column: "scenario_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_msels_team_id",
                table: "msels",
                column: "team_id");

            migrationBuilder.AddForeignKey(
                name: "FK_msels_teams_team_id",
                table: "msels",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_user_msel_roles_scenario_events_scenario_event_id",
                table: "user_msel_roles",
                column: "scenario_event_id",
                principalTable: "scenario_events",
                principalColumn: "id");
        }
    }
}
