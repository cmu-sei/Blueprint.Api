// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddMselCasterExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "msel_caster_exports",
                columns: table => new
                {
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_name = table.Column<string>(type: "text", nullable: true),
                    source_directory_id = table.Column<Guid>(type: "uuid", nullable: true),
                    archive = table.Column<byte[]>(type: "bytea", nullable: true),
                    date_exported = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    exported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_caster_exports", x => x.msel_id);
                    table.ForeignKey(
                        name: "FK_msel_caster_exports_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "msel_caster_export_directories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_caster_export_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    terraform_version = table.Column<string>(type: "text", nullable: true),
                    parallelism = table.Column<int>(type: "integer", nullable: true),
                    azure_destroy_failure_threshold_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    azure_destroy_failure_threshold = table.Column<int>(type: "integer", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_caster_export_directories", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_caster_export_directories_msel_caster_exports_msel_cas~",
                        column: x => x.msel_caster_export_id,
                        principalTable: "msel_caster_exports",
                        principalColumn: "msel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "msel_caster_export_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_caster_export_directory_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_caster_export_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_caster_export_files_msel_caster_export_directories_mse~",
                        column: x => x.msel_caster_export_directory_id,
                        principalTable: "msel_caster_export_directories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_msel_caster_export_directories_msel_caster_export_id",
                table: "msel_caster_export_directories",
                column: "msel_caster_export_id");

            migrationBuilder.CreateIndex(
                name: "IX_msel_caster_export_files_msel_caster_export_directory_id",
                table: "msel_caster_export_files",
                column: "msel_caster_export_directory_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "msel_caster_export_files");

            migrationBuilder.DropTable(
                name: "msel_caster_export_directories");

            migrationBuilder.DropTable(
                name: "msel_caster_exports");
        }
    }
}
