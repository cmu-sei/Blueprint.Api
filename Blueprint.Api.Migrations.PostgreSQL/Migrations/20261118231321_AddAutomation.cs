// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

﻿using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "automation_paused",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "automation_test_player_view_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "current_group_number",
                table: "msels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "current_group_started_at",
                table: "msels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "current_move_number",
                table: "msels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deployment_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "show_group_on_facilitator_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "show_integration_target_on_facilitator_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "show_move_on_facilitator_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "show_time_on_facilitator_view",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "on_facilitator_view",
                table: "data_fields",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // the Facilitator View starts out with the columns the Exercise View already shows
            migrationBuilder.Sql("UPDATE data_fields SET on_facilitator_view = on_exercise_view;");

            migrationBuilder.CreateTable(
                name: "automation_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<int>(type: "integer", nullable: false),
                    action_parameters = table.Column<Dictionary<string, string>>(type: "hstore", nullable: true),
                    secret_parameters = table.Column<string>(type: "text", nullable: true),
                    vm_mask = table.Column<string>(type: "text", nullable: true),
                    expected_output = table.Column<string>(type: "text", nullable: true),
                    expiration_seconds = table.Column<int>(type: "integer", nullable: false),
                    delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    iterations = table.Column<int>(type: "integer", nullable: false),
                    iteration_termination = table.Column<int>(type: "integer", nullable: false),
                    trigger_condition = table.Column<int>(type: "integer", nullable: false),
                    trigger_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    repeatable = table.Column<bool>(type: "boolean", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_automation_tasks_automation_tasks_trigger_task_id",
                        column: x => x.trigger_task_id,
                        principalTable: "automation_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_automation_tasks_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_automation_tasks_scenario_events_scenario_event_id",
                        column: x => x.scenario_event_id,
                        principalTable: "scenario_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "automation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    automation_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scenario_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task_name = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<int>(type: "integer", nullable: false),
                    config_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    parent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rerun_of_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    player_view_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    trigger_chain = table.Column<bool>(type: "boolean", nullable: false),
                    follow_on_task_ids = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    status_detail = table.Column<string>(type: "text", nullable: true),
                    current_iteration = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_automation_runs_automation_tasks_automation_task_id",
                        column: x => x.automation_task_id,
                        principalTable: "automation_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_automation_runs_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "automation_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    iteration = table.Column<int>(type: "integer", nullable: false),
                    vm_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vm_name = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    input_string = table.Column<string>(type: "text", nullable: true),
                    expected_output = table.Column<string>(type: "text", nullable: true),
                    actual_output = table.Column<string>(type: "text", nullable: true),
                    output_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    expiration_seconds = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_automation_results_automation_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "automation_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_automation_results_run_id_iteration",
                table: "automation_results",
                columns: new[] { "run_id", "iteration" });

            migrationBuilder.CreateIndex(
                name: "IX_automation_runs_automation_task_id_deployment_id",
                table: "automation_runs",
                columns: new[] { "automation_task_id", "deployment_id" },
                unique: true,
                filter: "source = 10");

            migrationBuilder.CreateIndex(
                name: "IX_automation_runs_msel_id_kind_date_created",
                table: "automation_runs",
                columns: new[] { "msel_id", "kind", "date_created" });

            migrationBuilder.CreateIndex(
                name: "IX_automation_runs_status_due_at",
                table: "automation_runs",
                columns: new[] { "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "IX_automation_tasks_msel_id",
                table: "automation_tasks",
                column: "msel_id");

            migrationBuilder.CreateIndex(
                name: "IX_automation_tasks_scenario_event_id_display_order",
                table: "automation_tasks",
                columns: new[] { "scenario_event_id", "display_order" });

            migrationBuilder.CreateIndex(
                name: "IX_automation_tasks_trigger_task_id",
                table: "automation_tasks",
                column: "trigger_task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_results");

            migrationBuilder.DropTable(
                name: "automation_runs");

            migrationBuilder.DropTable(
                name: "automation_tasks");

            migrationBuilder.DropColumn(
                name: "automation_paused",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "automation_test_player_view_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "current_group_number",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "current_group_started_at",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "current_move_number",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "deployment_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_group_on_facilitator_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_integration_target_on_facilitator_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_move_on_facilitator_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "show_time_on_facilitator_view",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "on_facilitator_view",
                table: "data_fields");
        }
    }
}
