using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class offset_data : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "delay_seconds",
                table: "scenario_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "delta_seconds",
                table: "scenario_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_event_id",
                table: "scenario_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "parent_event_status_trigger",
                table: "scenario_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_scenario_events_parent_event_id",
                table: "scenario_events",
                column: "parent_event_id");

            migrationBuilder.AddForeignKey(
                name: "FK_scenario_events_scenario_events_parent_event_id",
                table: "scenario_events",
                column: "parent_event_id",
                principalTable: "scenario_events",
                principalColumn: "id");
            // initialize delay_seconds from the existing row_index
            migrationBuilder.Sql("UPDATE scenario_events SET delta_seconds = row_index * 100");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_scenario_events_scenario_events_parent_event_id",
                table: "scenario_events");

            migrationBuilder.DropIndex(
                name: "IX_scenario_events_parent_event_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "delay_seconds",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "delta_seconds",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "parent_event_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "parent_event_status_trigger",
                table: "scenario_events");
        }
    }
}
