/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class optionalteams : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cite_actions_teams_team_id",
                table: "cite_actions");

            migrationBuilder.DropForeignKey(
                name: "FK_cite_roles_teams_team_id",
                table: "cite_roles");

            migrationBuilder.AlterColumn<Guid>(
                name: "team_id",
                table: "cite_roles",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "team_id",
                table: "cite_actions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_cite_actions_teams_team_id",
                table: "cite_actions",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_cite_roles_teams_team_id",
                table: "cite_roles",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cite_actions_teams_team_id",
                table: "cite_actions");

            migrationBuilder.DropForeignKey(
                name: "FK_cite_roles_teams_team_id",
                table: "cite_roles");

            migrationBuilder.AlterColumn<Guid>(
                name: "team_id",
                table: "cite_roles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "team_id",
                table: "cite_actions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_cite_actions_teams_team_id",
                table: "cite_actions",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_cite_roles_teams_team_id",
                table: "cite_roles",
                column: "team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
