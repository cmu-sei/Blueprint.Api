// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Cite.Api.Client;

namespace Blueprint.Api.Automation.Handlers
{
    /// <summary>
    /// The actions on the MSEL's own Player view, CITE evaluation and Gallery exhibit. They take their ids from the
    /// MSEL, so an author never types a URL, and decide their own success. A test of a CITE or Gallery action reports
    /// the change it would make rather than making it.
    /// </summary>
    public class BlueprintActionHandler : IAutomationActionHandler
    {
        private readonly IAutomationClientFactory _clients;

        public BlueprintActionHandler(IAutomationClientFactory clients)
        {
            _clients = clients;
        }

        public IReadOnlyCollection<AutomationAction> Actions { get; } = new[]
        {
            AutomationAction.player_notification,
            AutomationAction.cite_situation_update,
            AutomationAction.cite_move_set,
            AutomationAction.gallery_move_inject_set,
        };

        public Task<ActionOutcome> ExecuteAsync(AutomationActionContext context, CancellationToken ct) => context.Action switch
        {
            AutomationAction.player_notification => NotifyAsync(context, ct),
            AutomationAction.cite_situation_update => SituationAsync(context, ct),
            AutomationAction.cite_move_set => CiteMoveAsync(context, ct),
            AutomationAction.gallery_move_inject_set => GalleryMoveAsync(context, ct),
            _ => throw new AutomationConfigurationException($"{context.Action} is not one of Blueprint's own actions."),
        };

        private async Task<ActionOutcome> NotifyAsync(AutomationActionContext context, CancellationToken ct)
        {
            var viewId = context.PlayerViewId ?? throw new AutomationConfigurationException(
                context.IsTest ? "Choose a Player view to test against." : "The MSEL has no Player view; push it to Player first.");
            var text = context.Get("Text");
            if (string.IsNullOrWhiteSpace(text))
                throw new AutomationConfigurationException("The notification has no text.");
            var player = await _clients.PlayerAsync(ct);
            await player.BroadcastToViewAsync(viewId, new Player.Api.Client.Notification { Text = text }, ct);
            return new ActionOutcome($"Notification sent to Player view {viewId}.", true);
        }

        private async Task<ActionOutcome> SituationAsync(AutomationActionContext context, CancellationToken ct)
        {
            if (!DateTimeOffset.TryParse(context.Get("SituationTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                throw new AutomationConfigurationException("The situation time is not a date and time.");
            var description = context.Get("SituationDescription") ?? string.Empty;
            if (DryRun(context, context.CiteEvaluationId, "CITE evaluation", out var dry))
                return new ActionOutcome($"{dry} PUT evaluations/{context.CiteEvaluationId?.ToString() ?? "{citeEvaluationId}"}/situation with time {time:u}.", true);
            var cite = await _clients.CiteAsync(ct);
            var evaluation = await cite.UpdateEvaluationSituationAsync(context.CiteEvaluationId.Value,
                new EvaluationSituation { SituationTime = time, SituationDescription = description }, ct);
            return new ActionOutcome($"CITE evaluation {evaluation.Id} situation set to {evaluation.SituationTime:u}.", true);
        }

        private async Task<ActionOutcome> CiteMoveAsync(AutomationActionContext context, CancellationToken ct)
        {
            var move = context.GetInt("MoveNumber") ?? throw new AutomationConfigurationException("The move number is not a whole number.");
            if (DryRun(context, context.CiteEvaluationId, "CITE evaluation", out var dry))
                return new ActionOutcome($"{dry} PUT evaluations/{context.CiteEvaluationId?.ToString() ?? "{citeEvaluationId}"}/move/{move}.", true);
            var cite = await _clients.CiteAsync(ct);
            var evaluation = await cite.SetEvaluationCurrentMoveAsync(context.CiteEvaluationId.Value, move, ct);
            return new ActionOutcome($"CITE evaluation {evaluation.Id} is at move {evaluation.CurrentMoveNumber}.", evaluation.CurrentMoveNumber == move);
        }

        private async Task<ActionOutcome> GalleryMoveAsync(AutomationActionContext context, CancellationToken ct)
        {
            var move = context.GetInt("MoveNumber") ?? throw new AutomationConfigurationException("The move number is not a whole number.");
            var inject = context.GetInt("InjectNumber") ?? throw new AutomationConfigurationException("The inject number is not a whole number.");
            if (DryRun(context, context.GalleryExhibitId, "Gallery exhibit", out var dry))
                return new ActionOutcome($"{dry} PUT exhibits/{context.GalleryExhibitId?.ToString() ?? "{galleryExhibitId}"}/move/{move}/inject/{inject}.", true);
            var gallery = await _clients.GalleryAsync(ct);
            var exhibit = await gallery.SetExhibitMoveAndInjectAsync(context.GalleryExhibitId.Value, move, inject, ct);
            return new ActionOutcome($"Gallery exhibit {exhibit.Id} is at move {exhibit.CurrentMove}, inject {exhibit.CurrentInject}.",
                exhibit.CurrentMove == move && exhibit.CurrentInject == inject);
        }

        /// <summary>
        /// Whether to report the request instead of making it: a test of the action, or a test while the MSEL has not
        /// been pushed to the integration. Outside a test, a missing integration is an error.
        /// </summary>
        private static bool DryRun(AutomationActionContext context, Guid? id, string what, out string prefix)
        {
            prefix = "Dry run: would send";
            if (context.DryRun || (context.IsTest && id == null))
                return true;
            if (id == null)
                throw new AutomationConfigurationException($"The MSEL has no {what}; push it first.");
            return false;
        }
    }
}
