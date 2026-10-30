using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddMselScoringModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "msel_scoring_models",
                columns: table => new
                {
                    msel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    calculation_equation = table.Column<string>(type: "text", nullable: true),
                    hide_scores_on_score_sheet = table.Column<bool>(type: "boolean", nullable: false),
                    display_comment_text_boxes = table.Column<bool>(type: "boolean", nullable: false),
                    display_scoring_model_by_move_number = table.Column<bool>(type: "boolean", nullable: false),
                    show_past_situation_descriptions = table.Column<bool>(type: "boolean", nullable: false),
                    use_submit = table.Column<bool>(type: "boolean", nullable: false),
                    use_user_score = table.Column<bool>(type: "boolean", nullable: false),
                    use_team_score = table.Column<bool>(type: "boolean", nullable: false),
                    use_team_average_score = table.Column<bool>(type: "boolean", nullable: false),
                    use_type_average_score = table.Column<bool>(type: "boolean", nullable: false),
                    use_official_score = table.Column<bool>(type: "boolean", nullable: false),
                    right_side_display = table.Column<int>(type: "integer", nullable: false),
                    right_side_html_block = table.Column<string>(type: "text", nullable: true),
                    right_side_embedded_url = table.Column<string>(type: "text", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_scoring_models", x => x.msel_id);
                    table.ForeignKey(
                        name: "FK_msel_scoring_models_msels_msel_id",
                        column: x => x.msel_id,
                        principalTable: "msels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "msel_scoring_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_scoring_model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    calculation_equation = table.Column<string>(type: "text", nullable: true),
                    is_modifier_required = table.Column<bool>(type: "boolean", nullable: false),
                    scoring_weight = table.Column<double>(type: "double precision", nullable: false),
                    move_number_first_display = table.Column<int>(type: "integer", nullable: false),
                    move_number_last_display = table.Column<int>(type: "integer", nullable: false),
                    scoring_option_selection = table.Column<int>(type: "integer", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_scoring_categories", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_scoring_categories_msel_scoring_models_msel_scoring_mo~",
                        column: x => x.msel_scoring_model_id,
                        principalTable: "msel_scoring_models",
                        principalColumn: "msel_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "msel_scoring_options",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    msel_scoring_category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_modifier = table.Column<bool>(type: "boolean", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_msel_scoring_options", x => x.id);
                    table.ForeignKey(
                        name: "FK_msel_scoring_options_msel_scoring_categories_msel_scoring_c~",
                        column: x => x.msel_scoring_category_id,
                        principalTable: "msel_scoring_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_msel_scoring_categories_msel_scoring_model_id",
                table: "msel_scoring_categories",
                column: "msel_scoring_model_id");

            migrationBuilder.CreateIndex(
                name: "IX_msel_scoring_options_msel_scoring_category_id",
                table: "msel_scoring_options",
                column: "msel_scoring_category_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "msel_scoring_options");

            migrationBuilder.DropTable(
                name: "msel_scoring_categories");

            migrationBuilder.DropTable(
                name: "msel_scoring_models");
        }
    }
}
