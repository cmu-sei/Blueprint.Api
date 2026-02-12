// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Infrastructure.Extensions;
using TinCan;

namespace Blueprint.Api.Services
{
    public interface IXApiService
    {
        bool IsConfigured();
        Task<bool> CreateAsync(
            Uri verb,
            Dictionary<string, string> activityData,
            Dictionary<string, string> categoryData,
            List<Dictionary<string, string>> groupingData,
            Dictionary<string, string> parentData,
            Dictionary<string, string> otherData,
            Guid? mselId,
            Guid? teamId,
            CancellationToken ct);
        Task<bool> MselViewedAsync(MselEntity msel, CancellationToken ct);
        Task<bool> InjectViewedAsync(InjectEntity inject, CancellationToken ct);
    }

    public class XApiService : IXApiService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly XApiOptions _xApiOptions;
        private readonly IXApiQueueService _queueService;
        private readonly Agent _agent;
        private readonly AgentAccount _account;
        private readonly Context _xApiContext;
        private readonly ILogger<XApiService> _logger;

        public XApiService(
            BlueprintContext context,
            IPrincipal user,
            XApiOptions xApiOptions,
            IXApiQueueService queueService,
            ILogger<XApiService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _xApiOptions = xApiOptions;
            _queueService = queueService;
            _logger = logger;

            if (IsConfigured())
            {
                // configure AgentAccount
                _account = new TinCan.AgentAccount();
                _account.name = _user.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
                var iss = _user.Identities.First().Claims.First(c => c.Type == "iss")?.Value;
                if (!string.IsNullOrEmpty(_xApiOptions.IssuerUrl))
                {
                    _account.homePage = new Uri(_xApiOptions.IssuerUrl);
                }
                else if (iss.Contains("http"))
                {
                    _account.homePage = new Uri(iss);
                }
                else if (string.IsNullOrEmpty(_xApiOptions.IssuerUrl))
                {
                    _account.homePage = new Uri("http://" + iss);
                }

                // configure Agent
                _agent = new TinCan.Agent();
                _agent.name = _context.Users.Find(_user.GetId()).Name;
                _agent.account = _account;

                // Initialize the Context
                _xApiContext = new Context();
                _xApiContext.platform = _xApiOptions.Platform;
                _xApiContext.language = "en-US";
            }
        }

        public bool IsConfigured()
        {
            return !string.IsNullOrWhiteSpace(_xApiOptions.Username);
        }

        public async Task<bool> CreateAsync(
            Uri verbUri,
            Dictionary<string, string> activityData,
            Dictionary<string, string> categoryData,
            List<Dictionary<string, string>> groupingData,
            Dictionary<string, string> parentData,
            Dictionary<string, string> otherData,
            Guid? mselId,
            Guid? teamId,
            CancellationToken ct)
        {
            if (!IsConfigured())
            {
                _logger.LogInformation("xAPI Service not configured");
                return true;
            }

            var verb = new Verb();
            verb.id = verbUri;
            verb.display = new LanguageMap();
            verb.display.Add("en-US", verb.id.Segments.Last());

            var activity = new Activity();
            activity.id = _xApiOptions.ApiUrl + activityData["type"] + "/" + activityData["id"];
            activity.definition = new TinCan.ActivityDefinition();
            activity.definition.type = new Uri(activityData["activityType"]);
            if (activityData.ContainsKey("moreInfo"))
            {
                activity.definition.moreInfo = new Uri(_xApiOptions.UiUrl + activityData["moreInfo"]);
            }
            activity.definition.name = new LanguageMap();
            activity.definition.name.Add("en-US", activityData["name"]);
            activity.definition.description = new LanguageMap();
            activity.definition.description.Add("en-US", activityData["description"]);

            var context = new Context();
            context.platform = _xApiContext.platform;
            context.language = _xApiContext.language;

            // Set registration to MSEL ID if available (groups statements by MSEL session)
            if (mselId.HasValue && mselId.Value != Guid.Empty)
            {
                context.registration = mselId.Value;
            }

            if (teamId.HasValue && teamId.Value != Guid.Empty)
            {
                var team = _context.Teams.Find(teamId.Value);
                if (team != null)
                {
                    var group = new TinCan.Group();
                    group.name = team.ShortName;
                    if (!string.IsNullOrEmpty(_xApiOptions.EmailDomain))
                    {
                        group.mbox = "mailto:" + team.ShortName + "@" + _xApiOptions.EmailDomain;
                    }
                    group.account = new AgentAccount();
                    group.account.homePage = new Uri(_xApiOptions.UiUrl);
                    group.account.name = team.Id.ToString();
                    group.member = new List<Agent> { _agent };
                    context.team = group;
                }
            }

            var contextActivities = new ContextActivities();
            context.contextActivities = contextActivities;

            if (parentData != null && parentData.Count > 0)
            {
                var parent = new Activity();
                parent.id = _xApiOptions.ApiUrl + parentData["type"] + "/" + parentData["id"];
                parent.definition = new ActivityDefinition();
                parent.definition.name = new LanguageMap();
                parent.definition.name.Add("en-US", parentData["name"]);
                parent.definition.description = new LanguageMap();
                parent.definition.description.Add("en-US", parentData["description"]);
                parent.definition.type = new Uri(parentData["activityType"]);
                if (parentData.ContainsKey("moreInfo"))
                {
                    parent.definition.moreInfo = new Uri(_xApiOptions.UiUrl + parentData["moreInfo"]);
                }
                contextActivities.parent = new List<Activity>();
                contextActivities.parent.Add(parent);
            }

            if (otherData != null && otherData.Count > 0)
            {
                var other = new TinCan.Activity();
                other.id = _xApiOptions.ApiUrl + otherData["type"] + "/" + otherData["id"];
                other.definition = new ActivityDefinition();
                other.definition.name = new LanguageMap();
                other.definition.name.Add("en-US", otherData["name"]);
                other.definition.description = new LanguageMap();
                other.definition.description.Add("en-US", otherData["description"]);
                other.definition.type = new Uri(otherData["activityType"]);
                if (otherData.ContainsKey("moreInfo"))
                {
                    other.definition.moreInfo = new Uri(_xApiOptions.UiUrl + otherData["moreInfo"]);
                }
                contextActivities.other = new List<Activity>();
                context.contextActivities.other.Add(other);
            }

            if (groupingData != null && groupingData.Count > 0)
            {
                contextActivities.grouping = new List<Activity>();
                foreach (var groupingItem in groupingData)
                {
                    if (groupingItem.Count > 0)
                    {
                        var grouping = new TinCan.Activity();
                        grouping.id = _xApiOptions.ApiUrl + groupingItem["type"] + "/" + groupingItem["id"];
                        grouping.definition = new ActivityDefinition();
                        grouping.definition.name = new LanguageMap();
                        grouping.definition.name.Add("en-US", groupingItem["name"]);
                        grouping.definition.description = new LanguageMap();
                        grouping.definition.description.Add("en-US", groupingItem["description"]);
                        grouping.definition.type = new Uri(groupingItem["activityType"]);
                        if (groupingItem.ContainsKey("moreInfo") && !string.IsNullOrEmpty(groupingItem["moreInfo"]))
                        {
                            grouping.definition.moreInfo = new Uri(_xApiOptions.UiUrl + groupingItem["moreInfo"]);
                        }
                        context.contextActivities.grouping.Add(grouping);
                    }
                }
            }

            if (categoryData != null && categoryData.Count > 0)
            {
                var category = new TinCan.Activity();
                category.id = _xApiOptions.ApiUrl + categoryData["type"] + "/" + categoryData["id"];
                category.definition = new ActivityDefinition();
                category.definition.name = new LanguageMap();
                category.definition.name.Add("en-US", categoryData["name"]);
                category.definition.description = new LanguageMap();
                category.definition.description.Add("en-US", categoryData["description"]);
                category.definition.type = new Uri(categoryData["activityType"]);
                if (categoryData.ContainsKey("moreInfo"))
                {
                    category.definition.moreInfo = new Uri(_xApiOptions.UiUrl + categoryData["moreInfo"]);
                }
                contextActivities.category = new List<Activity>();
                context.contextActivities.category.Add(category);
            }

            var statement = new Statement();
            statement.actor = _agent;
            statement.verb = verb;
            statement.target = activity;
            statement.context = context;

            // Serialize statement to JSON and enqueue for background processing
            var statementJson = statement.ToJSON();

            var queuedStatement = new XApiQueuedStatementEntity
            {
                Id = Guid.NewGuid(),
                StatementJson = statementJson,
                Verb = verb.id.Segments.Last(),
                ActivityId = activity.id,
                MselId = mselId,
                TeamId = teamId
            };

            await _queueService.EnqueueAsync(queuedStatement, ct);
            _logger.LogInformation("Enqueued xAPI statement for verb {Verb}", verb.id.Segments.Last());

            return true;
        }

        public async Task<bool> MselViewedAsync(MselEntity msel, CancellationToken ct)
        {
            if (!IsConfigured())
            {
                return true;
            }

            var verb = new Uri("http://id.tincanapi.com/verb/viewed");

            var activity = new Dictionary<string, string>();
            activity.Add("id", msel.Id.ToString());
            activity.Add("name", msel.Name);
            activity.Add("description", msel.Description ?? "Mission Scenario Event List");
            activity.Add("type", "msel");
            activity.Add("activityType", "http://adlnet.gov/expapi/activities/simulation");
            activity.Add("moreInfo", "/msel/" + msel.Id.ToString());

            var category = new Dictionary<string, string>();
            category.Add("id", "planning");
            category.Add("name", "Planning");
            category.Add("description", "MSEL planning and design activities");
            category.Add("type", "category");
            category.Add("activityType", "http://id.tincanapi.com/activitytype/category");

            var parent = new Dictionary<string, string>();
            if (msel.OrganizationId.HasValue)
            {
                var organization = await _context.Organizations.FindAsync(msel.OrganizationId.Value);
                if (organization != null)
                {
                    parent.Add("id", organization.Id.ToString());
                    parent.Add("name", organization.Name);
                    parent.Add("description", organization.Description ?? "Organization");
                    parent.Add("type", "organization");
                    parent.Add("activityType", "http://id.tincanapi.com/activitytype/organization");
                }
            }

            var grouping = new List<Dictionary<string, string>>();
            var other = new Dictionary<string, string>();

            // Get user's team in this MSEL, if any
            Guid? teamId = null;
            var teamUser = await _context.TeamUsers
                .Where(tu => tu.UserId == _user.GetId() && tu.Team.MselId == msel.Id)
                .FirstOrDefaultAsync(ct);
            if (teamUser != null)
            {
                teamId = teamUser.TeamId;
            }

            return await CreateAsync(verb, activity, category, grouping, parent, other, msel.Id, teamId, ct);
        }

        public async Task<bool> InjectViewedAsync(InjectEntity inject, CancellationToken ct)
        {
            if (!IsConfigured())
            {
                return true;
            }

            var verb = new Uri("http://id.tincanapi.com/verb/viewed");

            var activity = new Dictionary<string, string>();
            activity.Add("id", inject.Id.ToString());
            activity.Add("name", inject.Description ?? "Inject");
            activity.Add("description", "MSEL inject/event");
            activity.Add("type", "inject");
            activity.Add("activityType", "http://id.tincanapi.com/activitytype/task");
            activity.Add("moreInfo", "/inject/" + inject.Id.ToString());

            var category = new Dictionary<string, string>();
            category.Add("id", "planning");
            category.Add("name", "Planning");
            category.Add("description", "MSEL planning and design activities");
            category.Add("type", "category");
            category.Add("activityType", "http://id.tincanapi.com/activitytype/category");

            // Get MSEL as parent
            var scenarioEvent = await _context.ScenarioEvents
                .Where(se => se.Id == inject.ScenarioEventId)
                .FirstOrDefaultAsync(ct);

            var parent = new Dictionary<string, string>();
            if (scenarioEvent != null)
            {
                var msel = await _context.Msels.FindAsync(scenarioEvent.MselId);
                if (msel != null)
                {
                    parent.Add("id", msel.Id.ToString());
                    parent.Add("name", msel.Name);
                    parent.Add("description", msel.Description ?? "Mission Scenario Event List");
                    parent.Add("type", "msel");
                    parent.Add("activityType", "http://adlnet.gov/expapi/activities/simulation");
                    parent.Add("moreInfo", "/msel/" + msel.Id.ToString());
                }
            }

            // Get Move as grouping context
            var grouping = new List<Dictionary<string, string>>();
            if (inject.MoveNumber.HasValue && scenarioEvent != null)
            {
                var move = await _context.Moves
                    .Where(m => m.MselId == scenarioEvent.MselId && m.MoveNumber == inject.MoveNumber.Value)
                    .FirstOrDefaultAsync(ct);
                if (move != null)
                {
                    var moveGrouping = new Dictionary<string, string>();
                    moveGrouping.Add("id", move.Id.ToString());
                    moveGrouping.Add("name", move.Title ?? $"Move {move.MoveNumber}");
                    moveGrouping.Add("description", move.Description ?? "MSEL Move/Phase");
                    moveGrouping.Add("type", "move");
                    moveGrouping.Add("activityType", "http://id.tincanapi.com/activitytype/collection-simple");
                    moveGrouping.Add("moreInfo", "");
                    grouping.Add(moveGrouping);
                }
            }

            var other = new Dictionary<string, string>();

            // Get user's team in this MSEL, if any
            Guid? teamId = null;
            Guid? mselId = scenarioEvent?.MselId;
            if (mselId.HasValue)
            {
                var teamUser = await _context.TeamUsers
                    .Where(tu => tu.UserId == _user.GetId() && tu.Team.MselId == mselId.Value)
                    .FirstOrDefaultAsync(ct);
                if (teamUser != null)
                {
                    teamId = teamUser.TeamId;
                }
            }

            return await CreateAsync(verb, activity, category, grouping, parent, other, mselId, teamId, ct);
        }
    }
}
