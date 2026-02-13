using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AddXApiQueuedStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "x_api_queued_statements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuid_generate_v4()"),
                    statement_json = table.Column<string>(type: "text", nullable: false),
                    queued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    verb = table.Column<string>(type: "text", nullable: true),
                    activity_id = table.Column<string>(type: "text", nullable: true),
                    msel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    date_created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    date_modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_api_queued_statements", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_api_queued_statements");
        }
    }
}
