/*
 Copyright 2025 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class steamfitterintegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "description",
                table: "scenario_events",
                newName: "integration_target");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:hstore", ",,")
                .Annotation("Npgsql:PostgresExtension:uuid-ossp", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:uuid-ossp", ",,");

            migrationBuilder.AddColumn<Guid>(
                name: "steamfitter_task_id",
                table: "scenario_events",
                type: "uuid",
                nullable: true);

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

            migrationBuilder.CreateTable(
                name: "steamfitter_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    scenario_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_type = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<int>(type: "integer", nullable: false),
                    vm_mask = table.Column<string>(type: "text", nullable: true),
                    api_url = table.Column<string>(type: "text", nullable: true),
                    action_parameters = table.Column<Dictionary<string, string>>(type: "hstore", nullable: true),
                    expected_output = table.Column<string>(type: "text", nullable: true),
                    expiration_seconds = table.Column<int>(type: "integer", nullable: false),
                    delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    iterations = table.Column<int>(type: "integer", nullable: false),
                    trigger_condition = table.Column<int>(type: "integer", nullable: false),
                    user_executable = table.Column<bool>(type: "boolean", nullable: false),
                    repeatable = table.Column<bool>(type: "boolean", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steamfitter_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_steamfitter_tasks_scenario_events_scenario_event_id",
                        column: x => x.scenario_event_id,
                        principalTable: "scenario_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scenario_events_id",
                table: "scenario_events",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_steamfitter_tasks_scenario_event_id",
                table: "steamfitter_tasks",
                column: "scenario_event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "steamfitter_tasks");

            migrationBuilder.DropIndex(
                name: "IX_scenario_events_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "steamfitter_task_id",
                table: "scenario_events");

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

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:uuid-ossp", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:uuid-ossp", ",,");
        }
    }
}
