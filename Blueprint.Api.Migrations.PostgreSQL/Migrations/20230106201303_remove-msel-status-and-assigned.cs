using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class removemselstatusandassigned : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_scenario_events_teams_assigned_team_id",
                table: "scenario_events");

            migrationBuilder.DropIndex(
                name: "IX_scenario_events_assigned_team_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "assigned_team_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "status",
                table: "scenario_events");

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "short_name",
                table: "organizations",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "short_name",
                table: "organizations");

            migrationBuilder.AddColumn<Guid>(
                name: "assigned_team_id",
                table: "scenario_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "scenario_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_scenario_events_assigned_team_id",
                table: "scenario_events",
                column: "assigned_team_id");

            migrationBuilder.AddForeignKey(
                name: "FK_scenario_events_teams_assigned_team_id",
                table: "scenario_events",
                column: "assigned_team_id",
                principalTable: "teams",
                principalColumn: "id");
        }
    }
}
