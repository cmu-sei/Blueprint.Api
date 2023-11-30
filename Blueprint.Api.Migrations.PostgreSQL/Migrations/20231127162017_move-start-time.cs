/*
 Copyright 2023 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class movestarttime : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "move_stop_time",
                table: "moves");

            migrationBuilder.DropColumn(
                name: "title",
                table: "moves");

            migrationBuilder.AddColumn<int>(
                name: "delta_seconds",
                table: "moves",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            // set initial delta_seconds values, because move_start_time will no longer be used
            migrationBuilder.Sql("UPDATE moves SET delta_seconds = COALESCE( (DATE_PART('hour', move_start_time) * 3600) + (DATE_PART('minute', move_start_time) * 60) + (DATE_PART('second', move_start_time)), 0)");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delta_seconds",
                table: "moves");

            migrationBuilder.AddColumn<DateTime>(
                name: "move_stop_time",
                table: "moves",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "moves",
                type: "text",
                nullable: true);
        }
    }
}
