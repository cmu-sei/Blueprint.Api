/*
 Copyright 2023 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
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
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
