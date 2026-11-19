// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

﻿using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceSteamfitterWithAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each Steamfitter task becomes an automation task of its event, keeping its id. Blueprint's own task types
            // become Blueprint's own actions, aimed at the MSEL's integrations: the push filled in their URLs and bodies,
            // and stored them back, so only the parameters the author wrote are kept. An http task's headers are a
            // secret now; AutomationSecretUpgrader encrypts them at startup. A task was pushed only when its event
            // targeted Steamfitter, so only those are enabled.
            migrationBuilder.Sql(@"
INSERT INTO automation_tasks (id, msel_id, scenario_event_id, display_order, name, description, action,
    action_parameters, secret_parameters, vm_mask, expected_output, expiration_seconds, delay_seconds,
    interval_seconds, iterations, iteration_termination, trigger_condition, trigger_task_id, enabled, repeatable,
    date_created, date_modified, created_by, modified_by)
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
    st.date_created, st.date_modified, st.created_by, st.modified_by
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
    true, t.repeatable, t.date_created, t.date_modified, t.created_by, t.modified_by
FROM automation_tasks t
WHERE t.action IN (11, 12, 13, 14, 100, 110, 400)
ORDER BY t.scenario_event_id, t.display_order;");
            migrationBuilder.Sql(@"
UPDATE scenario_events se SET steamfitter_task_id = st.id FROM steamfitter_tasks st WHERE st.scenario_event_id = se.id;");
            migrationBuilder.Sql(@"DELETE FROM automation_tasks WHERE id IN (SELECT id FROM steamfitter_tasks);");
        }
    }
}
