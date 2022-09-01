/*
 Copyright 2022Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class org_templates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations");

            migrationBuilder.AlterColumn<Guid>(
                name: "msel_id",
                table: "organizations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "is_template",
                table: "organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "is_template",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "summary",
                table: "organizations");

            migrationBuilder.AlterColumn<Guid>(
                name: "msel_id",
                table: "organizations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_organizations_msels_msel_id",
                table: "organizations",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
