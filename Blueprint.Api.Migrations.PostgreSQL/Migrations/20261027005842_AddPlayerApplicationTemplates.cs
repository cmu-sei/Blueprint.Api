// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerApplicationTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "msel_id",
                table: "player_applications",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "is_template",
                table: "player_applications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "system_roles",
                keyColumn: "id",
                keyValue: new Guid("1da3027e-725d-4753-9455-a836ed9bdb1e"),
                column: "permissions",
                value: new[] { 2, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // a template has no MSEL, so it cannot survive msel_id becoming required again
            migrationBuilder.Sql("DELETE FROM player_applications WHERE msel_id IS NULL;");

            migrationBuilder.DropColumn(
                name: "is_template",
                table: "player_applications");

            migrationBuilder.AlterColumn<Guid>(
                name: "msel_id",
                table: "player_applications",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.UpdateData(
                table: "system_roles",
                keyColumn: "id",
                keyValue: new Guid("1da3027e-725d-4753-9455-a836ed9bdb1e"),
                column: "permissions",
                value: new[] { 2, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27 });
        }
    }
}
