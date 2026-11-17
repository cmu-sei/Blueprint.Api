using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddTechniques : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "technique_releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "text", nullable: true),
                    is_defensive = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    release_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technique_releases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "technique_tactics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technique_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stix_id = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    short_name = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "text", nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technique_tactics", x => x.id);
                    table.ForeignKey(
                        name: "FK_technique_tactics_technique_releases_technique_release_id",
                        column: x => x.technique_release_id,
                        principalTable: "technique_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "techniques",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technique_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stix_id = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "text", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tactics = table.Column<string>(type: "text", nullable: true),
                    platforms = table.Column<string>(type: "text", nullable: true),
                    weaknesses = table.Column<string>(type: "text", nullable: true),
                    reference_links = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_techniques", x => x.id);
                    table.ForeignKey(
                        name: "FK_techniques_technique_releases_technique_release_id",
                        column: x => x.technique_release_id,
                        principalTable: "technique_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_techniques_techniques_parent_id",
                        column: x => x.parent_id,
                        principalTable: "techniques",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "msel_techniques",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technique_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_techniques", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_techniques_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_msel_techniques_techniques_technique_id",
                        column: x => x.technique_id,
                        principalTable: "techniques",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technique_countermeasures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offensive_external_id = table.Column<string>(type: "text", nullable: true),
                    defensive_technique_id = table.Column<Guid>(type: "uuid", nullable: false),
                    defensive_tactic = table.Column<string>(type: "text", nullable: true),
                    rationale = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technique_countermeasures", x => x.id);
                    table.ForeignKey(
                        name: "FK_technique_countermeasures_technique_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "technique_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_technique_countermeasures_techniques_defensive_technique_id",
                        column: x => x.defensive_technique_id,
                        principalTable: "techniques",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_msel_techniques_msel_id_technique_id",
                table: "msel_techniques",
                columns: new[] { "msel_id", "technique_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_msel_techniques_technique_id",
                table: "msel_techniques",
                column: "technique_id");

            migrationBuilder.CreateIndex(
                name: "IX_technique_countermeasures_defensive_technique_id",
                table: "technique_countermeasures",
                column: "defensive_technique_id");

            migrationBuilder.CreateIndex(
                name: "IX_technique_countermeasures_release_id_offensive_external_id_~",
                table: "technique_countermeasures",
                columns: new[] { "release_id", "offensive_external_id", "defensive_technique_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_technique_releases_domain_version",
                table: "technique_releases",
                columns: new[] { "domain", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_technique_tactics_technique_release_id_external_id",
                table: "technique_tactics",
                columns: new[] { "technique_release_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_techniques_parent_id",
                table: "techniques",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_techniques_technique_release_id_external_id",
                table: "techniques",
                columns: new[] { "technique_release_id", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "msel_techniques");

            migrationBuilder.DropTable(
                name: "technique_countermeasures");

            migrationBuilder.DropTable(
                name: "technique_tactics");

            migrationBuilder.DropTable(
                name: "techniques");

            migrationBuilder.DropTable(
                name: "technique_releases");
        }
    }
}
