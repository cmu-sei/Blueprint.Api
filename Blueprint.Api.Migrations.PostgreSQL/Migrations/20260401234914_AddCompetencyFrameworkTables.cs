using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddCompetencyFrameworkTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "competency_frameworks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competency_frameworks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "competency_elements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    competency_framework_id = table.Column<Guid>(type: "uuid", nullable: false),
                    element_identifier = table.Column<string>(type: "text", nullable: true),
                    element_type = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competency_elements", x => x.id);
                    table.ForeignKey(
                        name: "FK_competency_elements_competency_elements_parent_id",
                        column: x => x.parent_id,
                        principalTable: "competency_elements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_competency_elements_competency_frameworks_competency_framew~",
                        column: x => x.competency_framework_id,
                        principalTable: "competency_frameworks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "proficiency_scales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    competency_framework_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proficiency_scales", x => x.id);
                    table.ForeignKey(
                        name: "FK_proficiency_scales_competency_frameworks_competency_framewo~",
                        column: x => x.competency_framework_id,
                        principalTable: "competency_frameworks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "proficiency_levels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    proficiency_scale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    value = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proficiency_levels", x => x.id);
                    table.ForeignKey(
                        name: "FK_proficiency_levels_proficiency_scales_proficiency_scale_id",
                        column: x => x.proficiency_scale_id,
                        principalTable: "proficiency_scales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "system_roles",
                keyColumn: "id",
                keyValue: new Guid("1da3027e-725d-4753-9455-a836ed9bdb1e"),
                column: "permissions",
                value: new[] { 2, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27 });

            migrationBuilder.CreateIndex(
                name: "IX_competency_elements_competency_framework_id_element_identif~",
                table: "competency_elements",
                columns: new[] { "competency_framework_id", "element_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_competency_elements_parent_id",
                table: "competency_elements",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_competency_frameworks_name_version",
                table: "competency_frameworks",
                columns: new[] { "name", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_proficiency_levels_proficiency_scale_id",
                table: "proficiency_levels",
                column: "proficiency_scale_id");

            migrationBuilder.CreateIndex(
                name: "IX_proficiency_scales_competency_framework_id",
                table: "proficiency_scales",
                column: "competency_framework_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "competency_elements");

            migrationBuilder.DropTable(
                name: "proficiency_levels");

            migrationBuilder.DropTable(
                name: "proficiency_scales");

            migrationBuilder.DropTable(
                name: "competency_frameworks");

            migrationBuilder.UpdateData(
                table: "system_roles",
                keyColumn: "id",
                keyValue: new Guid("1da3027e-725d-4753-9455-a836ed9bdb1e"),
                column: "permissions",
                value: new[] { 2, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25 });
        }
    }
}
