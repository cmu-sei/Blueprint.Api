/*
 Copyright 2026 Carnegie Mellon University. All Rights Reserved.
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RemoveIntegrationTargetDataFieldType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Convert any existing DataFields with data_type = 160 (IntegrationTarget)
            // to data_type = 0 (String) so they remain functional after removing the enum value.
            migrationBuilder.Sql(@"
                UPDATE data_fields
                SET data_type = 0
                WHERE data_type = 160;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No reversal needed - we can't determine which String fields were formerly IntegrationTarget
        }
    }
}
