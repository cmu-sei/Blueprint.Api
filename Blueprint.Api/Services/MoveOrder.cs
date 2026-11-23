// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Every move starts strictly after the move numbered before it, and every scenario event starts at or after
    /// the first move. Saving moves and events that break this is allowed, because a MSEL changes a lot while it
    /// is edited; pushing integrations is refused until it holds. A move number the MSEL already uses is left to
    /// the database's unique index.
    /// </summary>
    public static class MoveOrder
    {
        /// <summary>
        /// Each pair of consecutive moves (by number) that share a start time or are out of order, for a
        /// MSEL saved before the rule, e.g. an imported one.
        /// </summary>
        public static List<string> FindProblems(IEnumerable<MoveEntity> moves)
        {
            var byNumber = moves.OrderBy(m => m.MoveNumber).ToList();
            var problems = new List<string>();
            for (var i = 1; i < byNumber.Count; i++)
            {
                var previous = byNumber[i - 1];
                var move = byNumber[i];
                if (move.DeltaSeconds == previous.DeltaSeconds)
                    problems.Add($"Move {previous.MoveNumber} and Move {move.MoveNumber} start at the same time.");
                else if (move.DeltaSeconds < previous.DeltaSeconds)
                    problems.Add($"Move {move.MoveNumber} starts before Move {previous.MoveNumber}.");
            }
            return problems;
        }

        /// <summary>
        /// Everything that stops a push: the move order problems, plus each scenario event that starts before
        /// the first move, so it belongs to no move. A MSEL with no moves has none of either.
        /// </summary>
        public static List<string> FindPushProblems(IEnumerable<MoveEntity> moves, IEnumerable<int> eventDeltaSeconds)
        {
            var moveList = moves.ToList();
            var problems = FindProblems(moveList);
            if (moveList.Count > 0)
            {
                var first = moveList.OrderBy(m => m.MoveNumber).First();
                var early = eventDeltaSeconds.Count(d => d < first.DeltaSeconds);
                if (early > 0)
                    problems.Add(early == 1
                        ? $"1 scenario event starts before Move {first.MoveNumber}, so it is in no move."
                        : $"{early} scenario events start before Move {first.MoveNumber}, so they are in no move.");
            }
            return problems;
        }
    }
}
