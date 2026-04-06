using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddCompetencyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // competency_frameworks (final schema: original + Moodle columns + default scale FK)
            migrationBuilder.CreateTable(
                name: "competency_frameworks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    name = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    description_format = table.Column<int>(type: "integer", nullable: false),
                    id_number = table.Column<string>(type: "text", nullable: true),
                    scale_values = table.Column<string>(type: "text", nullable: true),
                    scale_configuration = table.Column<string>(type: "text", nullable: true),
                    taxonomies = table.Column<string>(type: "text", nullable: true),
                    default_proficiency_scale_id = table.Column<Guid>(type: "uuid", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competency_frameworks", x => x.id);
                });

            // proficiency_scales (standalone — no framework FK)
            migrationBuilder.CreateTable(
                name: "proficiency_scales",
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
                    table.PrimaryKey("PK_proficiency_scales", x => x.id);
                });

            // proficiency_levels
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

            // competencies (Moodle-aligned)
            migrationBuilder.CreateTable(
                name: "competencies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    competency_framework_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    id_number = table.Column<string>(type: "text", nullable: true),
                    short_name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    description_format = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    rule_type = table.Column<string>(type: "text", nullable: true),
                    rule_outcome = table.Column<int>(type: "integer", nullable: false),
                    rule_config = table.Column<string>(type: "text", nullable: true),
                    scale_values = table.Column<string>(type: "text", nullable: true),
                    scale_configuration = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competencies", x => x.id);
                    table.ForeignKey(
                        name: "FK_competencies_competencies_parent_id",
                        column: x => x.parent_id,
                        principalTable: "competencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_competencies_competency_frameworks_competency_framework_id",
                        column: x => x.competency_framework_id,
                        principalTable: "competency_frameworks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // competency_relationships
            migrationBuilder.CreateTable(
                name: "competency_relationships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    competency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    related_competency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_competency_relationships", x => x.id);
                    table.ForeignKey(
                        name: "FK_competency_relationships_competencies_competency_id",
                        column: x => x.competency_id,
                        principalTable: "competencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_competency_relationships_competencies_related_competency_id",
                        column: x => x.related_competency_id,
                        principalTable: "competencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Update Observer role permissions to include competency view permissions
            migrationBuilder.UpdateData(
                table: "system_roles",
                keyColumn: "id",
                keyValue: new Guid("1da3027e-725d-4753-9455-a836ed9bdb1e"),
                column: "permissions",
                value: new[] { 2, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27 });

            // Indexes
            migrationBuilder.CreateIndex(
                name: "IX_competency_frameworks_default_proficiency_scale_id",
                table: "competency_frameworks",
                column: "default_proficiency_scale_id");

            migrationBuilder.CreateIndex(
                name: "IX_competency_frameworks_id_number",
                table: "competency_frameworks",
                column: "id_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_proficiency_scales_name",
                table: "proficiency_scales",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_proficiency_levels_proficiency_scale_id",
                table: "proficiency_levels",
                column: "proficiency_scale_id");

            migrationBuilder.CreateIndex(
                name: "IX_competencies_competency_framework_id_id_number",
                table: "competencies",
                columns: new[] { "competency_framework_id", "id_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_competencies_parent_id",
                table: "competencies",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_competency_relationships_competency_id_related_competency_id",
                table: "competency_relationships",
                columns: new[] { "competency_id", "related_competency_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_competency_relationships_related_competency_id",
                table: "competency_relationships",
                column: "related_competency_id");

            // FK: competency_frameworks → proficiency_scales (default scale)
            migrationBuilder.AddForeignKey(
                name: "FK_competency_frameworks_proficiency_scales_default_proficienc~",
                table: "competency_frameworks",
                column: "default_proficiency_scale_id",
                principalTable: "proficiency_scales",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "competency_relationships");

            migrationBuilder.DropTable(
                name: "competencies");

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
