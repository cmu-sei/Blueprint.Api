/*
Copyright 2021 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class cellformating : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scenario_event_number",
                table: "scenario_events");

            migrationBuilder.RenameColumn(
                name: "group",
                table: "scenario_events",
                newName: "time");

            migrationBuilder.AddColumn<string>(
                name: "cell_metadata",
                table: "data_values",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cell_metadata",
                table: "data_fields",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "column_metadata",
                table: "data_fields",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cell_metadata",
                table: "data_values");

            migrationBuilder.DropColumn(
                name: "cell_metadata",
                table: "data_fields");

            migrationBuilder.DropColumn(
                name: "column_metadata",
                table: "data_fields");

            migrationBuilder.RenameColumn(
                name: "time",
                table: "scenario_events",
                newName: "group");

            migrationBuilder.AddColumn<int>(
                name: "scenario_event_number",
                table: "scenario_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
