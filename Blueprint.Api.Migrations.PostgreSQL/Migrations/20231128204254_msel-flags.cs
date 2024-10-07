/*
 Copyright 2023 Carnegie Mellon University. All Rights Reserved.
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class Mselflags : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "show_group_on_exercise_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_group_on_scenario_event_list",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_move_on_exercise_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_move_on_scenario_event_list",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_time_on_exercise_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_time_on_scenario_event_list",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "show_group_on_exercise_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_group_on_scenario_event_list",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_move_on_exercise_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_move_on_scenario_event_list",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_time_on_exercise_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_time_on_scenario_event_list",
                table: "msels");
        }
    }
}
