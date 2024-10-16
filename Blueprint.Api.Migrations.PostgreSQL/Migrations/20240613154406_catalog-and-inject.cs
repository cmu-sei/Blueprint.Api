/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved.
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class Catalogandinject : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_data_values_scenario_event_id_data_field_id",
                table: "data_values");

            migrationBuilder.DropColumn(
                name: "old_team_id",
                table: "units");

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "scenario_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "inject_id",
                table: "scenario_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "scenario_event_type",
                table: "scenario_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<Guid>(
                name: "scenario_event_id",
                table: "data_values",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "inject_id",
                table: "data_values",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "inject_type_id",
                table: "data_fields",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "inject_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inject_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "catalogs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    inject_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalogs", x => x.id);
                    table.ForeignKey(
                        name: "FK_catalogs_catalogs_parent_id",
                        column: x => x.parent_id,
                        principalTable: "catalogs",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_catalogs_inject_types_inject_type_id",
                        column: x => x.inject_type_id,
                        principalTable: "inject_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_units", x => x.id);
                    table.ForeignKey(
                        name: "FK_catalog_units_catalogs_catalog_id",
                        column: x => x.catalog_id,
                        principalTable: "catalogs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_catalog_units_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "injects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    inject_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requires_inject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    catalog_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_injects", x => x.id);
                    table.ForeignKey(
                        name: "FK_injects_catalogs_catalog_entity_id",
                        column: x => x.catalog_entity_id,
                        principalTable: "catalogs",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_injects_inject_types_inject_type_id",
                        column: x => x.inject_type_id,
                        principalTable: "inject_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_injects_injects_requires_inject_id",
                        column: x => x.requires_inject_id,
                        principalTable: "injects",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "catalog_injects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    inject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_injects", x => x.id);
                    table.ForeignKey(
                        name: "FK_catalog_injects_catalogs_catalog_id",
                        column: x => x.catalog_id,
                        principalTable: "catalogs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_catalog_injects_injects_inject_id",
                        column: x => x.inject_id,
                        principalTable: "injects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scenario_events_inject_id",
                table: "scenario_events",
                column: "inject_id");

            migrationBuilder.CreateIndex(
                name: "IX_data_values_inject_id",
                table: "data_values",
                column: "inject_id");

            migrationBuilder.CreateIndex(
                name: "IX_data_values_scenario_event_id_inject_id_data_field_id",
                table: "data_values",
                columns: new[] { "scenario_event_id", "inject_id", "data_field_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_values_data_value_scenario_event_or_inject",
                table: "data_values",
                sql: "(scenario_event_id IS NOT NULL AND inject_id IS NULL) OR (scenario_event_id IS NULL AND inject_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_data_fields_inject_type_id",
                table: "data_fields",
                column: "inject_type_id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_data_fields_data_field_msel_or_inject_type",
                table: "data_fields",
                sql: "(msel_id IS NOT NULL AND inject_type_id IS NULL) OR (msel_id IS NULL AND inject_type_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_injects_catalog_id",
                table: "catalog_injects",
                column: "catalog_id");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_injects_inject_id_catalog_id",
                table: "catalog_injects",
                columns: new[] { "inject_id", "catalog_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_units_catalog_id",
                table: "catalog_units",
                column: "catalog_id");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_units_unit_id_catalog_id",
                table: "catalog_units",
                columns: new[] { "unit_id", "catalog_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalogs_inject_type_id",
                table: "catalogs",
                column: "inject_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_catalogs_name",
                table: "catalogs",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalogs_parent_id",
                table: "catalogs",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_inject_types_name",
                table: "inject_types",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_injects_catalog_entity_id",
                table: "injects",
                column: "catalog_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_injects_inject_type_id",
                table: "injects",
                column: "inject_type_id");

            migrationBuilder.CreateIndex(
                name: "IX_injects_requires_inject_id",
                table: "injects",
                column: "requires_inject_id");

            migrationBuilder.AddForeignKey(
                name: "FK_data_fields_inject_types_inject_type_id",
                table: "data_fields",
                column: "inject_type_id",
                principalTable: "inject_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_data_values_injects_inject_id",
                table: "data_values",
                column: "inject_id",
                principalTable: "injects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_scenario_events_injects_inject_id",
                table: "scenario_events",
                column: "inject_id",
                principalTable: "injects",
                principalColumn: "id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_data_fields_inject_types_inject_type_id",
                table: "data_fields");

            migrationBuilder.DropForeignKey(
                name: "FK_data_values_injects_inject_id",
                table: "data_values");

            migrationBuilder.DropForeignKey(
                name: "FK_scenario_events_injects_inject_id",
                table: "scenario_events");

            migrationBuilder.DropTable(
                name: "catalog_injects");

            migrationBuilder.DropTable(
                name: "catalog_units");

            migrationBuilder.DropTable(
                name: "injects");

            migrationBuilder.DropTable(
                name: "catalogs");

            migrationBuilder.DropTable(
                name: "inject_types");

            migrationBuilder.DropIndex(
                name: "IX_scenario_events_inject_id",
                table: "scenario_events");

            migrationBuilder.DropIndex(
                name: "IX_data_values_inject_id",
                table: "data_values");

            migrationBuilder.DropIndex(
                name: "IX_data_values_scenario_event_id_inject_id_data_field_id",
                table: "data_values");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_values_data_value_scenario_event_or_inject",
                table: "data_values");

            migrationBuilder.DropIndex(
                name: "IX_data_fields_inject_type_id",
                table: "data_fields");

            migrationBuilder.DropCheckConstraint(
                name: "CK_data_fields_data_field_msel_or_inject_type",
                table: "data_fields");

            migrationBuilder.DropColumn(
                name: "description",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "inject_id",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "scenario_event_type",
                table: "scenario_events");

            migrationBuilder.DropColumn(
                name: "inject_id",
                table: "data_values");

            migrationBuilder.DropColumn(
                name: "inject_type_id",
                table: "data_fields");

            migrationBuilder.AddColumn<Guid>(
                name: "old_team_id",
                table: "units",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "scenario_event_id",
                table: "data_values",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_data_values_scenario_event_id_data_field_id",
                table: "data_values",
                columns: new[] { "scenario_event_id", "data_field_id" },
                unique: true);
        }
    }
}
