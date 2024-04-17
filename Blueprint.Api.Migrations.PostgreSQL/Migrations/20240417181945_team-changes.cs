/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class teamchanges : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE from teams where msel_id is null;");
            migrationBuilder.Sql("DELETE from teams where msel_id NOT IN (SELECT id from msels);");

            migrationBuilder.DropColumn(
                name: "old_team_id",
                table: "teams");

            migrationBuilder.AlterColumn<Guid>(
                name: "msel_id",
                table: "teams",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_teams_msels_msel_id",
                table: "teams",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_teams_msels_msel_id",
                table: "teams");

            migrationBuilder.AlterColumn<Guid>(
                name: "msel_id",
                table: "teams",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "old_team_id",
                table: "teams",
                type: "uuid",
                nullable: true);
        }
    }
}
