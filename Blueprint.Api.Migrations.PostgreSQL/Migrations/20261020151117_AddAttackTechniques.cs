using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddAttackTechniques : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attack_releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("PK_attack_releases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "attack_tactics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attack_release_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_attack_tactics", x => x.id);
                    table.ForeignKey(
                        name: "FK_attack_tactics_attack_releases_attack_release_id",
                        column: x => x.attack_release_id,
                        principalTable: "attack_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "attack_techniques",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attack_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stix_id = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "text", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tactics = table.Column<string>(type: "text", nullable: true),
                    platforms = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attack_techniques", x => x.id);
                    table.ForeignKey(
                        name: "FK_attack_techniques_attack_releases_attack_release_id",
                        column: x => x.attack_release_id,
                        principalTable: "attack_releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_attack_techniques_attack_techniques_parent_id",
                        column: x => x.parent_id,
                        principalTable: "attack_techniques",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "msel_attack_techniques",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attack_technique_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_attack_techniques", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_attack_techniques_attack_techniques_attack_technique_id",
                        column: x => x.attack_technique_id,
                        principalTable: "attack_techniques",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_msel_attack_techniques_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attack_releases_domain_version",
                table: "attack_releases",
                columns: new[] { "domain", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attack_tactics_attack_release_id_external_id",
                table: "attack_tactics",
                columns: new[] { "attack_release_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attack_techniques_attack_release_id_external_id",
                table: "attack_techniques",
                columns: new[] { "attack_release_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attack_techniques_parent_id",
                table: "attack_techniques",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_msel_attack_techniques_attack_technique_id",
                table: "msel_attack_techniques",
                column: "attack_technique_id");

            migrationBuilder.CreateIndex(
                name: "IX_msel_attack_techniques_msel_id_attack_technique_id",
                table: "msel_attack_techniques",
                columns: new[] { "msel_id", "attack_technique_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attack_tactics");

            migrationBuilder.DropTable(
                name: "msel_attack_techniques");

            migrationBuilder.DropTable(
                name: "attack_techniques");

            migrationBuilder.DropTable(
                name: "attack_releases");
        }
    }
}
