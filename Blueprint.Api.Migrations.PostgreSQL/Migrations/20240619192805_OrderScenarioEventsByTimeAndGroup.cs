/*
 Copyright 2024 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blueprint.Api.Migrations.PostgreSQL.Migrations
{
    public partial class OrderScenarioEventsByTimeAndGroup : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"with duped_grps as (
                    select msel_id, delta_seconds
                    from scenario_events ise
                    where exists
                    (select 1
                    from scenario_events 
                    where msel_id = ise.msel_id and delta_seconds = ise.delta_seconds and
                    group_order = ise.group_order
                    and id != ise.id
                    )),
                affected_evts as (
                    select se.msel_id, se.id,
                    row_number () over(partition by se.msel_id, se.delta_seconds order by se.group_order, se.date_created) -1 as row_num 
                    from scenario_events se
                    where exists (
                        select msel_id, delta_seconds
                        from duped_grps where se.msel_id = msel_id and se.delta_seconds = delta_seconds
                    )
                )
                update scenario_events as se
                set group_order=ae.row_num
                from affected_evts ae
                where se.msel_id=ae.msel_id and se.id=ae.id;"
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
