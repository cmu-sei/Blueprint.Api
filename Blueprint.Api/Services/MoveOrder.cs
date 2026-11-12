// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Every move starts strictly after the move numbered before it. A move number the MSEL already uses
    /// is left to the database's unique index.
    /// </summary>
    public static class MoveOrder
    {
        private const string Rule = "Each move must start after the move before it.";

        /// <summary>
        /// Why a move with this number and start can't sit among the MSEL's other moves, or null if it can.
        /// The conflict named is the closest one by move number.
        /// </summary>
        public static string FindConflict(int moveNumber, int deltaSeconds, IEnumerable<MoveEntity> otherMoves)
        {
            var others = otherMoves.Where(m => m.MoveNumber != moveNumber).ToList();
            var before = others
                .Where(m => m.MoveNumber < moveNumber && m.DeltaSeconds >= deltaSeconds)
                .OrderByDescending(m => m.MoveNumber)
                .FirstOrDefault();
            if (before != null)
                return before.DeltaSeconds == deltaSeconds
                    ? $"Move {moveNumber} can't start at the same time as Move {before.MoveNumber}. {Rule}"
                    : $"Move {moveNumber} must start after Move {before.MoveNumber}. {Rule}";
            var after = others
                .Where(m => m.MoveNumber > moveNumber && m.DeltaSeconds <= deltaSeconds)
                .OrderBy(m => m.MoveNumber)
                .FirstOrDefault();
            if (after != null)
                return after.DeltaSeconds == deltaSeconds
                    ? $"Move {moveNumber} can't start at the same time as Move {after.MoveNumber}. {Rule}"
                    : $"Move {moveNumber} must start before Move {after.MoveNumber}. {Rule}";
            return null;
        }

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
    }
}
