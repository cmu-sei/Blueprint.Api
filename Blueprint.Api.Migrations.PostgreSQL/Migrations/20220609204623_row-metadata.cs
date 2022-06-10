/*
Copyright 2021 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class rowmetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "row_index",
                table: "scenario_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "row_metadata",
                table: "scenario_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "header_row_metadata",
                table: "msels",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "row_index",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "row_metadata",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "header_row_metadata",
                table: "msels");
        }
    }
}
