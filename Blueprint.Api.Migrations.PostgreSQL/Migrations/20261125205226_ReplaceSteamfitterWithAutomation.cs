// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Blueprint runs automation tasks itself: their tables, the MSEL's execution and Facilitator View columns, and the
    /// conversion of each scenario event's Steamfitter task into an automation task, after which the Steamfitter
    /// table and columns are dropped.
    /// </summary>
    public partial class ReplaceSteamfitterWithAutomation : Migration
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

            // the tables, in their final shape
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
                    score = table.Column<int>(type: "integer", nullable: false),
                    user_executable = table.Column<bool>(type: "boolean", nullable: false),
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
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
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

            // Each Steamfitter task becomes an automation task of its event, keeping its id. Blueprint's own task types
            // become Blueprint's own actions, aimed at the MSEL's integrations: the push filled in their URLs and bodies,
            // and stored them back, so only the parameters the author wrote are kept. An http task's headers are a
            // secret now; AutomationSecretUpgrader encrypts them at startup. A task was pushed only when its event
            // targeted Steamfitter, so only those are enabled. A task a participant could run in Steamfitter still can.
            migrationBuilder.Sql(@"
INSERT INTO automation_tasks (id, msel_id, scenario_event_id, display_order, name, description, action,
    action_parameters, secret_parameters, vm_mask, expected_output, expiration_seconds, delay_seconds,
    interval_seconds, iterations, iteration_termination, trigger_condition, trigger_task_id, enabled, repeatable,
    score, user_executable, date_created, date_modified, created_by, modified_by)
SELECT st.id, se.msel_id, st.scenario_event_id, 0, COALESCE(NULLIF(st.name, ''), 'Steamfitter task'), st.description,
    CASE st.task_type WHEN 1 THEN 100 WHEN 2 THEN 400 WHEN 3 THEN 110 ELSE st.task_type END,
    CASE st.task_type
        WHEN 1 THEN
            CASE WHEN COALESCE(st.action_parameters -> 'notificationText', '') <> ''
                THEN hstore('Text', st.action_parameters -> 'notificationText') ELSE ''::hstore END
        WHEN 3 THEN
            (CASE WHEN COALESCE(st.action_parameters -> 'situationTime', '') <> ''
                THEN hstore('SituationTime', st.action_parameters -> 'situationTime') ELSE ''::hstore END)
            || (CASE WHEN COALESCE(st.action_parameters -> 'situationDescription', '') <> ''
                THEN hstore('SituationDescription', st.action_parameters -> 'situationDescription') ELSE ''::hstore END)
        WHEN 2 THEN slice(COALESCE(st.action_parameters, ''::hstore), ARRAY['EmailFrom', 'EmailTo', 'EmailCC', 'Subject', 'Message', 'Mime'])
        ELSE slice(COALESCE(st.action_parameters, ''::hstore), ARRAY['Url', 'Body'])
    END,
    CASE WHEN st.task_type IN (11, 12, 13, 14) AND COALESCE(st.action_parameters -> 'Headers', '') <> ''
        THEN json_build_object('Headers', st.action_parameters -> 'Headers')::text END,
    NULLIF(st.vm_mask, ''),
    CASE WHEN st.task_type IN (11, 12, 13, 14) THEN NULLIF(st.expected_output, '') END,
    GREATEST(st.expiration_seconds, 0), GREATEST(st.delay_seconds, 0), GREATEST(st.interval_seconds, 0),
    GREATEST(st.iterations, 1), 0, 0, NULL,
    COALESCE(se.integration_target, '') ILIKE '%Steamfitter%', st.repeatable,
    0, st.user_executable, st.date_created, st.date_modified, st.created_by, st.modified_by
FROM steamfitter_tasks st
JOIN scenario_events se ON se.id = st.scenario_event_id
WHERE st.task_type IN (1, 2, 3, 11, 12, 13, 14);");

            // The push ran a group's tasks one after another once a facilitator started the group, whatever trigger
            // each said: within each group (the events sharing a time), each converted task runs on completion of the
            // one before it, and the first is run by hand. MselJsonUpgrader does the same for an imported file.
            migrationBuilder.Sql(@"
WITH ordered AS (
    SELECT t.id, LAG(t.id) OVER (PARTITION BY se.msel_id, se.delta_seconds ORDER BY se.group_order, se.id) AS previous
    FROM automation_tasks t
    JOIN scenario_events se ON se.id = t.scenario_event_id
    WHERE t.enabled AND t.id IN (SELECT id FROM steamfitter_tasks)
)
UPDATE automation_tasks t
SET trigger_condition = 40, trigger_task_id = o.previous
FROM ordered o
WHERE t.id = o.id AND o.previous IS NOT NULL;");

            // A MSEL deployed before the upgrade was pushed with no deployment of Blueprint's own, which its automation
            // runs are kept by: it gets one now, so it can be run from the Facilitator View without a pull and a push.
            migrationBuilder.Sql(@"
UPDATE msels SET deployment_id = uuid_generate_v4() WHERE status = 40 AND deployment_id IS NULL;");

            // Steamfitter is no longer a place an event is delivered to
            migrationBuilder.Sql(@"
UPDATE scenario_events
SET integration_target = NULLIF(array_to_string(array_remove(
    string_to_array(regexp_replace(integration_target, '\s*,\s*', ',', 'g'), ','), 'Steamfitter'), ', '), '')
WHERE integration_target ILIKE '%Steamfitter%';");

            migrationBuilder.DropTable(
                name: "steamfitter_tasks");

            migrationBuilder.DropIndex(
                name: "IX_scenario_events_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "steamfitter_scenario_role",
                table: "user_msel_roles");

            migrationBuilder.DropColumn(
                name: "steamfitter_task_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "steamfitter_integration_type",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "steamfitter_scenario_id",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "steamfitter_scenario_start_mode",
                table: "msels");

            migrationBuilder.DropColumn(
                name: "use_steamfitter",
                table: "msels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "steamfitter_scenario_role",
                table: "user_msel_roles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "steamfitter_task_id",
                table: "scenario_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "steamfitter_integration_type",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "steamfitter_scenario_id",
                table: "msels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "steamfitter_scenario_start_mode",
                table: "msels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "use_steamfitter",
                table: "msels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "steamfitter_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    scenario_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<int>(type: "integer", nullable: false),
                    action_parameters = table.Column<Dictionary<string, string>>(type: "hstore", nullable: true),
                    api_url = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    expected_output = table.Column<string>(type: "text", nullable: true),
                    expiration_seconds = table.Column<int>(type: "integer", nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    iterations = table.Column<int>(type: "integer", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    repeatable = table.Column<bool>(type: "boolean", nullable: false),
                    task_type = table.Column<int>(type: "integer", nullable: false),
                    trigger_condition = table.Column<int>(type: "integer", nullable: false),
                    user_executable = table.Column<bool>(type: "boolean", nullable: false),
                    vm_mask = table.Column<string>(type: "text", nullable: true)
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

            // Best effort, and lossy: each event gets back its first task Steamfitter could have run.
            migrationBuilder.Sql(@"
INSERT INTO steamfitter_tasks (id, scenario_event_id, task_type, name, description, action, action_parameters, vm_mask,
    api_url, expected_output, expiration_seconds, delay_seconds, interval_seconds, iterations, trigger_condition,
    user_executable, repeatable, date_created, date_modified, created_by, modified_by)
SELECT DISTINCT ON (t.scenario_event_id) t.id, t.scenario_event_id,
    CASE t.action WHEN 100 THEN 1 WHEN 400 THEN 2 WHEN 110 THEN 3 ELSE t.action END,
    t.name, t.description, CASE WHEN t.action IN (11, 12, 13, 14) THEN t.action ELSE 12 END,
    CASE t.action
        WHEN 100 THEN hstore('notificationText', COALESCE(t.action_parameters -> 'Text', ''))
        WHEN 110 THEN hstore(ARRAY['situationTime', 'situationDescription'],
            ARRAY[COALESCE(t.action_parameters -> 'SituationTime', ''), COALESCE(t.action_parameters -> 'SituationDescription', '')])
        ELSE t.action_parameters
    END,
    t.vm_mask, 'http', t.expected_output, t.expiration_seconds, t.delay_seconds, t.interval_seconds, t.iterations, 6,
    t.user_executable, t.repeatable, t.date_created, t.date_modified, t.created_by, t.modified_by
FROM automation_tasks t
WHERE t.action IN (11, 12, 13, 14, 100, 110, 400)
ORDER BY t.scenario_event_id, t.display_order;");
            migrationBuilder.Sql(@"
UPDATE scenario_events se SET steamfitter_task_id = st.id FROM steamfitter_tasks st WHERE st.scenario_event_id = se.id;");
            migrationBuilder.Sql(@"DELETE FROM automation_tasks WHERE id IN (SELECT id FROM steamfitter_tasks);");

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
