/*
 Copyright 2023 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class cascadedeletes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations");

            migrationBuilder.AddForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations");

            migrationBuilder.AddForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id");
        }
    }
}
