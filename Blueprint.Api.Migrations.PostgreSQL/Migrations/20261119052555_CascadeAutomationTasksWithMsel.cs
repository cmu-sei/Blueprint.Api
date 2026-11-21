// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class CascadeAutomationTasksWithMsel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_automation_tasks_msels_msel_id",
                table: "automation_tasks");

            migrationBuilder.AddForeignKey(
                name: "FK_automation_tasks_msels_msel_id",
                table: "automation_tasks",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_automation_tasks_msels_msel_id",
                table: "automation_tasks");

            migrationBuilder.AddForeignKey(
                name: "FK_automation_tasks_msels_msel_id",
                table: "automation_tasks",
                column: "msel_id",
                principalTable: "msels",
                principalColumn: "id");
        }
    }
}
