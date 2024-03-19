/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class morecascadedeletes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cite_actions_teams_team_id",
                table: "cite_actions");

            migrationBuilder.DropForeignKey(
                name: "FK_cite_roles_teams_team_id",
                table: "cite_roles");

            migrationBuilder.DropForeignKey(
                name: "FK_teams_msels_msel_id",
                table: "teams");

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
                name: "FK_cite_actions_teams_team_id",
                table: "cite_actions");

            migrationBuilder.DropForeignKey(
                name: "FK_cite_roles_teams_team_id",
                table: "cite_roles");

            migrationBuilder.DropForeignKey(
                name: "FK_teams_msels_msel_id",
                table: "teams");

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

            migrationBuilder.AddForeignKey(
                name: "FK_teams_msels_msel_id",
                table: "teams",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id");
        }
    }
}
