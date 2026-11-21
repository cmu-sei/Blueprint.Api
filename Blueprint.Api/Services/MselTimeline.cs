// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Where each scenario event falls in a MSEL's run: its move (the move's own number) and its group, the
    /// index of its time among the distinct event times of that move. These are the numbers a Gallery article
    /// is pushed with (an exhibit shows an article once its current move and inject reach them), so starting
    /// a move or group on the Facilitator View, and the Facilitator View's headers, use them too.
    /// </summary>
    public static class MselTimeline
    {
        /// <summary>
        /// The move and group of every event, by event id, as <c>[moveNumber, groupNumber]</c>. With no moves,
        /// every event is in move 0. Moves sharing a start time (saved before that was refused) are taken in
        /// number order, so an event at that time goes to the higher-numbered move, as Blueprint shows it.
        /// </summary>
        public static Dictionary<Guid, int[]> Positions(IEnumerable<ScenarioEventEntity> scenarioEvents, IEnumerable<MoveEntity> moves)
        {
            var positions = new Dictionary<Guid, int[]>();
            // order scenario events and moves by DeltaSeconds
            var events = scenarioEvents.OrderBy(se => se.DeltaSeconds).ToArray();
            var orderedMoves = moves.OrderBy(m => m.DeltaSeconds).ThenBy(m => m.MoveNumber).ToArray();
            var m = 0;  // move index
            var inject = 0;  // inject value
            var deltaSeconds = events.Length > 0 ? events[0].DeltaSeconds : 0;  // value of the previous scenario event.  Used to determine the inject number.
            // loop through the chronological scenario events
            for (int s = 0; s < events.Length; s++)
            {
                // if not on the last move, check this scenario event time to determine if it is in the current move
                if (orderedMoves.Length == 0 || m == orderedMoves.Length - 1 || events[s].DeltaSeconds < orderedMoves[m + 1].DeltaSeconds)
                {
                    if (events[s].DeltaSeconds != deltaSeconds)
                    {
                        inject++;
                    }
                }
                else
                {
                    // the move must be incremented
                    while (m < orderedMoves.Length - 1 && events[s].DeltaSeconds >= orderedMoves[m + 1].DeltaSeconds)
                    {
                        m++;  // increment the move
                    }
                    inject = 0;  // start with inject 0 for this new move
                }
                // with no moves every event is in move 0
                var moveNumber = orderedMoves.Length > m ? orderedMoves[m].MoveNumber : 0;
                deltaSeconds = events[s].DeltaSeconds;
                positions.Add(events[s].Id, new int[] { moveNumber, inject });
            }

            return positions;
        }
    }
}
