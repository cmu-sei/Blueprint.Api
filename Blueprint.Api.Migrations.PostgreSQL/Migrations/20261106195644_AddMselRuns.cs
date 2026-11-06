// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddMselRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "msel_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    player_view_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cite_evaluation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cite_scoring_model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gallery_collection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gallery_exhibit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    steamfitter_scenario_id = table.Column<Guid>(type: "uuid", nullable: true),
                    caster_workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    caster_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actual_start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    actual_end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    msel_snapshot_json = table.Column<string>(type: "text", nullable: true),
                    record_json = table.Column<string>(type: "text", nullable: true),
                    cite_submissions_json = table.Column<string>(type: "text", nullable: true),
                    statistics = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_runs_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_msel_runs_msel_id",
                table: "msel_runs",
                column: "msel_id");

            // One run for every MSEL that holds integration ids, so a MSEL deployed before runs existed reads
            // and pulls through its run like any other. A MSEL still Pushing (35), which is also where a failed
            // push stays, gets a Deploying (10) run; any other gets an Active (20) one. When the push completed
            // was never recorded, so actual_start_time stays empty rather than guessed.
            migrationBuilder.Sql(BackfillSql);
        }

        private const string BackfillSql = """
            INSERT INTO msel_runs (
                id, msel_id, status,
                player_view_id, cite_evaluation_id, cite_scoring_model_id,
                gallery_collection_id, gallery_exhibit_id, steamfitter_scenario_id,
                caster_workspace_id, caster_run_id,
                date_created, created_by)
            SELECT
                uuid_generate_v4(), m.id, CASE WHEN m.status = 35 THEN 10 ELSE 20 END,
                m.player_view_id, m.cite_evaluation_id, m.cite_scoring_model_id,
                m.gallery_collection_id, m.gallery_exhibit_id, m.steamfitter_scenario_id,
                m.caster_workspace_id, m.caster_run_id,
                now(), m.created_by
            FROM msels m
            WHERE (m.player_view_id IS NOT NULL
                OR m.cite_evaluation_id IS NOT NULL
                OR m.gallery_collection_id IS NOT NULL
                OR m.gallery_exhibit_id IS NOT NULL
                OR m.steamfitter_scenario_id IS NOT NULL
                OR m.caster_workspace_id IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM msel_runs r WHERE r.msel_id = m.id);
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "msel_runs");
        }
    }
}
