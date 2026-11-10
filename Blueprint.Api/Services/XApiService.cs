// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
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
        Task<bool> MselViewedAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<bool> ExerciseStartedAsync(MselEntity msel, CancellationToken ct);
        Task<bool> ExerciseStoppedAsync(MselEntity msel, CancellationToken ct);
        Task<bool> JoinPageViewedAsync(CancellationToken ct);
        Task<bool> MselJoinedAsync(MselEntity msel, CancellationToken ct);
        Task<string> GetStatementsAsync(Guid mselId, DateTime? since, DateTime? until, int limit, string source, bool hasSystemPermission, CancellationToken ct);
        Task<bool> AssertCompetencyAsync(ViewModels.CompetencyAssertion assertion, bool hasSystemPermission, CancellationToken ct);
        Task<bool> RecordCheckboxChangeAsync(Guid mselId, Guid eventId, Guid dataFieldId, string dataFieldName, bool isChecked, CancellationToken ct);
    }

    public class XApiService : IXApiService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly XApiOptions _xApiOptions;
        private readonly IXApiQueueService _queueService;
        private readonly Infrastructure.Options.ClientOptions _clientOptions;
        private Agent _agent;
        private AgentAccount _account;
        private Context _xApiContext;
        private readonly ILogger<XApiService> _logger;

        public XApiService(
            BlueprintContext context,
            IPrincipal user,
            XApiOptions xApiOptions,
            IXApiQueueService queueService,
            Infrastructure.Options.ClientOptions clientOptions,
            ILogger<XApiService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _xApiOptions = xApiOptions;
            _queueService = queueService;
            _clientOptions = clientOptions;
            _logger = logger;
        }

        private void EnsureAgentInitialized()
        {
            if (_agent != null || !IsConfigured())
                return;

            // configure AgentAccount
            _account = new TinCan.AgentAccount();
            _account.Name = _user.Identities.First().Claims.First(c => c.Type == "sub")?.Value;
            var iss = _user.Identities.First().Claims.First(c => c.Type == "iss")?.Value;
            if (!string.IsNullOrEmpty(_xApiOptions.IssuerUrl))
            {
                _account.HomePage = new Uri(_xApiOptions.IssuerUrl);
            }
            else if (iss.Contains("http"))
            {
                _account.HomePage = new Uri(iss);
            }
            else if (string.IsNullOrEmpty(_xApiOptions.IssuerUrl))
            {
                _account.HomePage = new Uri("http://" + iss);
            }

            // configure Agent
            _agent = new TinCan.Agent();
            _agent.Name = _context.Users.Where(u => u.Id == _user.GetId()).Select(u => u.Name).FirstOrDefault();
            _agent.Account = _account;

            // Initialize the Context
            _xApiContext = new Context();
            _xApiContext.Platform = _xApiOptions.Platform;
            _xApiContext.Language = "en-US";
        }

        public bool IsConfigured()
        {
            return _xApiOptions.Enabled && !string.IsNullOrWhiteSpace(_xApiOptions.Username);
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

            // Initialize agent lazily (only when needed, not in constructor)
            EnsureAgentInitialized();

            var verb = new Verb();
            verb.Id = verbUri;
            verb.Display = new LanguageMap();
            verb.Display.Add("en-US", verb.Id.Segments.Last());

            var activity = new Activity();
            activity.Id = _xApiOptions.ApiUrl + activityData["type"] + "/" + activityData["id"];
            activity.Definition = new TinCan.ActivityDefinition();
            activity.Definition.Type = new Uri(activityData["activityType"]);
            if (activityData.ContainsKey("moreInfo"))
            {
                activity.Definition.MoreInfo = new Uri(_xApiOptions.UiUrl + activityData["moreInfo"]);
            }
            activity.Definition.Name = new LanguageMap();
            activity.Definition.Name.Add("en-US", activityData["name"]);
            activity.Definition.Description = new LanguageMap();
            activity.Definition.Description.Add("en-US", activityData["description"]);

            var context = new Context();
            context.Platform = _xApiContext.Platform;
            context.Language = _xApiContext.Language;

            // Set registration to MSEL ID if available (groups statements by MSEL session)
            if (mselId.HasValue && mselId.Value != Guid.Empty)
            {
                context.Registration = mselId.Value;
            }

            if (teamId.HasValue && teamId.Value != Guid.Empty)
            {
                var team = _context.Teams.Find(teamId.Value);
                if (team != null)
                {
                    var group = new TinCan.Group();
                    group.Name = team.ShortName;
                    if (!string.IsNullOrEmpty(_xApiOptions.EmailDomain))
                    {
                        group.Mbox = "mailto:" + team.ShortName + "@" + _xApiOptions.EmailDomain;
                    }
                    group.Account = new AgentAccount();
                    group.Account.HomePage = new Uri(_xApiOptions.UiUrl);
                    group.Account.Name = team.Id.ToString();
                    group.Member = new List<Agent> { _agent };
                    context.Team = group;
                }
            }

            var contextActivities = new ContextActivities();
            context.ContextActivities = contextActivities;

            if (parentData != null && parentData.Count > 0)
            {
                var parent = new Activity();
                parent.Id = _xApiOptions.ApiUrl + parentData["type"] + "/" + parentData["id"];
                parent.Definition = new ActivityDefinition();
                parent.Definition.Name = new LanguageMap();
                parent.Definition.Name.Add("en-US", parentData["name"]);
                parent.Definition.Description = new LanguageMap();
                parent.Definition.Description.Add("en-US", parentData["description"]);
                parent.Definition.Type = new Uri(parentData["activityType"]);
                if (parentData.ContainsKey("moreInfo"))
                {
                    parent.Definition.MoreInfo = new Uri(_xApiOptions.UiUrl + parentData["moreInfo"]);
                }
                contextActivities.Parent = new List<Activity>();
                contextActivities.Parent.Add(parent);
            }

            if (otherData != null && otherData.Count > 0)
            {
                var other = new TinCan.Activity();
                other.Id = _xApiOptions.ApiUrl + otherData["type"] + "/" + otherData["id"];
                other.Definition = new ActivityDefinition();
                other.Definition.Name = new LanguageMap();
                other.Definition.Name.Add("en-US", otherData["name"]);
                other.Definition.Description = new LanguageMap();
                other.Definition.Description.Add("en-US", otherData["description"]);
                other.Definition.Type = new Uri(otherData["activityType"]);
                if (otherData.ContainsKey("moreInfo"))
                {
                    other.Definition.MoreInfo = new Uri(_xApiOptions.UiUrl + otherData["moreInfo"]);
                }
                contextActivities.Other = new List<Activity>();
                context.ContextActivities.Other.Add(other);
            }

            if (groupingData != null && groupingData.Count > 0)
            {
                contextActivities.Grouping = new List<Activity>();
                foreach (var groupingItem in groupingData)
                {
                    if (groupingItem.Count > 0)
                    {
                        var grouping = new TinCan.Activity();
                        // Use apiUrl from groupingItem if available, otherwise use Blueprint API URL
                        var apiUrl = groupingItem.ContainsKey("apiUrl") ? groupingItem["apiUrl"] : _xApiOptions.ApiUrl;
                        grouping.Id = apiUrl + groupingItem["type"] + "/" + groupingItem["id"];
                        grouping.Definition = new ActivityDefinition();
                        grouping.Definition.Name = new LanguageMap();
                        grouping.Definition.Name.Add("en-US", groupingItem["name"]);
                        grouping.Definition.Description = new LanguageMap();
                        grouping.Definition.Description.Add("en-US", groupingItem["description"]);
                        grouping.Definition.Type = new Uri(groupingItem["activityType"]);
                        if (groupingItem.ContainsKey("moreInfo") && !string.IsNullOrEmpty(groupingItem["moreInfo"]))
                        {
                            grouping.Definition.MoreInfo = new Uri(_xApiOptions.UiUrl + groupingItem["moreInfo"]);
                        }
                        context.ContextActivities.Grouping.Add(grouping);
                    }
                }
            }

            if (categoryData != null && categoryData.Count > 0)
            {
                var category = new TinCan.Activity();
                category.Id = _xApiOptions.ApiUrl + categoryData["type"] + "/" + categoryData["id"];
                category.Definition = new ActivityDefinition();
                category.Definition.Name = new LanguageMap();
                category.Definition.Name.Add("en-US", categoryData["name"]);
                category.Definition.Description = new LanguageMap();
                category.Definition.Description.Add("en-US", categoryData["description"]);
                category.Definition.Type = new Uri(categoryData["activityType"]);
                if (categoryData.ContainsKey("moreInfo"))
                {
                    category.Definition.MoreInfo = new Uri(_xApiOptions.UiUrl + categoryData["moreInfo"]);
                }
                contextActivities.Category = new List<Activity>();
                context.ContextActivities.Category.Add(category);
            }

            var statement = new Statement();
            statement.Actor = _agent;
            statement.Verb = verb;
            statement.Target = activity;
            statement.Context = context;

            // Serialize statement to JSON and enqueue for background processing
            var statementJson = statement.ToJson();

            var queuedStatement = new XApiQueuedStatementEntity
            {
                Id = Guid.NewGuid(),
                StatementJson = statementJson,
                Verb = verb.Id.Segments.Last(),
                ActivityId = activity.Id,
                MselId = mselId,
                TeamId = teamId
            };

            await _queueService.EnqueueAsync(queuedStatement, ct);
            _logger.LogInformation("Enqueued xAPI statement for verb {Verb}", verb.Id.Segments.Last());

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
            var grouping = BuildIntegrationGroupings(msel);
            var other = new Dictionary<string, string>();
            var teamId = await GetUserTeamIdAsync(msel.Id, ct);

            return await CreateAsync(verb, activity, category, grouping, parent, other, msel.Id, teamId, ct);
        }

        public async Task<bool> MselViewedAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.FindAsync(id, ct);
            if (msel == null)
            {
                return false;
            }
            if (!hasSystemPermission && !await MselUserRequirement.IsMet(_user.GetId(), msel.Id, _context))
                throw new ForbiddenException();

            return await MselViewedAsync(msel, ct);
        }

        public async Task<bool> ExerciseStartedAsync(MselEntity msel, CancellationToken ct)
        {
            if (!IsConfigured())
            {
                return true;
            }

            var verb = new Uri("http://adlnet.gov/expapi/verbs/launched");

            var activity = new Dictionary<string, string>();
            activity.Add("id", msel.Id.ToString());
            activity.Add("name", msel.Name);
            activity.Add("description", msel.Description ?? "Mission Scenario Event List");
            activity.Add("type", "msel");
            activity.Add("activityType", "http://adlnet.gov/expapi/activities/simulation");
            activity.Add("moreInfo", "/msel/" + msel.Id.ToString());

            var category = new Dictionary<string, string>();
            category.Add("id", "execution");
            category.Add("name", "Execution");
            category.Add("description", "MSEL execution and training activities");
            category.Add("type", "category");
            category.Add("activityType", "http://id.tincanapi.com/activitytype/category");

            var parent = new Dictionary<string, string>();
            var grouping = BuildIntegrationGroupings(msel);
            var other = new Dictionary<string, string>();
            var teamId = await GetUserTeamIdAsync(msel.Id, ct);

            return await CreateAsync(verb, activity, category, grouping, parent, other, msel.Id, teamId, ct);
        }

        public async Task<bool> ExerciseStoppedAsync(MselEntity msel, CancellationToken ct)
        {
            if (!IsConfigured())
            {
                return true;
            }

            var verb = new Uri("http://adlnet.gov/expapi/verbs/terminated");

            var activity = new Dictionary<string, string>();
            activity.Add("id", msel.Id.ToString());
            activity.Add("name", msel.Name);
            activity.Add("description", msel.Description ?? "Mission Scenario Event List");
            activity.Add("type", "msel");
            activity.Add("activityType", "http://adlnet.gov/expapi/activities/simulation");
            activity.Add("moreInfo", "/msel/" + msel.Id.ToString());

            var category = new Dictionary<string, string>();
            category.Add("id", "execution");
            category.Add("name", "Execution");
            category.Add("description", "MSEL execution and training activities");
            category.Add("type", "category");
            category.Add("activityType", "http://id.tincanapi.com/activitytype/category");

            var parent = new Dictionary<string, string>();
            var grouping = BuildIntegrationGroupings(msel);
            var other = new Dictionary<string, string>();
            var teamId = await GetUserTeamIdAsync(msel.Id, ct);

            return await CreateAsync(verb, activity, category, grouping, parent, other, msel.Id, teamId, ct);
        }

        public async Task<bool> JoinPageViewedAsync(CancellationToken ct)
        {
            if (!IsConfigured())
            {
                return true;
            }

            var verb = new Uri("http://id.tincanapi.com/verb/viewed");

            var activity = new Dictionary<string, string>();
            activity.Add("id", "join-page");
            activity.Add("name", "Join Event Page");
            activity.Add("description", "Join an event landing page");
            activity.Add("type", "page");
            activity.Add("activityType", "http://activitystrea.ms/schema/1.0/page");
            activity.Add("moreInfo", "/join");

            var category = new Dictionary<string, string>();
            category.Add("id", "navigation");
            category.Add("name", "Navigation");
            category.Add("description", "Page navigation and browsing activities");
            category.Add("type", "category");
            category.Add("activityType", "http://id.tincanapi.com/activitytype/category");

            var parent = new Dictionary<string, string>();
            var grouping = new List<Dictionary<string, string>>();
            var other = new Dictionary<string, string>();

            return await CreateAsync(verb, activity, category, grouping, parent, other, null, null, ct);
        }

        public async Task<bool> MselJoinedAsync(MselEntity msel, CancellationToken ct)
        {
            if (!IsConfigured())
            {
                return true;
            }

            var verb = new Uri("http://activitystrea.ms/schema/1.0/join");

            var activity = new Dictionary<string, string>();
            activity.Add("id", msel.Id.ToString());
            activity.Add("name", msel.Name);
            activity.Add("description", msel.Description ?? "Mission Scenario Event List");
            activity.Add("type", "msel");
            activity.Add("activityType", "http://adlnet.gov/expapi/activities/simulation");
            activity.Add("moreInfo", "/msel/" + msel.Id.ToString());

            var category = new Dictionary<string, string>();
            category.Add("id", "execution");
            category.Add("name", "Execution");
            category.Add("description", "MSEL execution and training activities");
            category.Add("type", "category");
            category.Add("activityType", "http://id.tincanapi.com/activitytype/category");

            var parent = new Dictionary<string, string>();
            var grouping = BuildIntegrationGroupings(msel);
            var other = new Dictionary<string, string>();
            var teamId = await GetUserTeamIdAsync(msel.Id, ct);

            return await CreateAsync(verb, activity, category, grouping, parent, other, msel.Id, teamId, ct);
        }

        private async Task<Guid?> GetUserTeamIdAsync(Guid mselId, CancellationToken ct)
        {
            return await _context.TeamUsers
                .Where(tu => tu.UserId == _user.GetId() && tu.Team.MselId == mselId)
                .Select(tu => (Guid?)tu.TeamId)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<bool> AssertCompetencyAsync(ViewModels.CompetencyAssertion assertion, bool hasSystemPermission, CancellationToken ct)
        {
            if (!IsConfigured())
            {
                _logger.LogInformation("xAPI Service not configured");
                return true;
            }

            EnsureAgentInitialized();

            var msel = await _context.Msels.FirstOrDefaultAsync(m => m.Id == assertion.MselId, ct);
            if (msel == null)
                throw new ArgumentException($"MSEL {assertion.MselId} not found");

            // only MSEL owners and evaluators can assert competencies
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context) &&
                !await EvaluatorRequirement.IsMet(_user.GetId(), msel.Id, _context))
                throw new ForbiddenException();

            if (assertion.TeamId.HasValue && assertion.TeamId.Value != Guid.Empty &&
                !await _context.Teams.AnyAsync(t => t.Id == assertion.TeamId.Value && t.MselId == msel.Id, ct))
                throw new ArgumentException($"Team {assertion.TeamId} not found on MSEL {assertion.MselId}");

            var competency = await _context.Competencies
                .Include(c => c.CompetencyFramework)
                .FirstOrDefaultAsync(c => c.Id == assertion.CompetencyId, ct);
            if (competency == null)
                throw new ArgumentException($"Competency {assertion.CompetencyId} not found");

            var proficiencyLevel = await _context.ProficiencyLevels
                .Include(pl => pl.ProficiencyScale)
                .FirstOrDefaultAsync(pl => pl.Id == assertion.ProficiencyLevelId, ct);
            if (proficiencyLevel == null)
                throw new ArgumentException($"Proficiency level {assertion.ProficiencyLevelId} not found");

            Data.Models.ScenarioEventEntity scenarioEvent = null;
            if (assertion.ScenarioEventId.HasValue && assertion.ScenarioEventId.Value != Guid.Empty)
            {
                scenarioEvent = await _context.ScenarioEvents.FirstOrDefaultAsync(se => se.Id == assertion.ScenarioEventId.Value && se.MselId == msel.Id, ct);
                if (scenarioEvent == null)
                    throw new ArgumentException($"Scenario event {assertion.ScenarioEventId} not found on MSEL {assertion.MselId}");
            }

            var scale = proficiencyLevel.ProficiencyScale;
            var allLevels = await _context.ProficiencyLevels
                .Where(pl => pl.ProficiencyScaleId == scale.Id)
                .ToListAsync(ct);
            var minValue = allLevels.Min(pl => pl.Value);
            var maxValue = allLevels.Max(pl => pl.Value);

            var verb = new Verb();
            verb.Id = new Uri("https://w3id.org/xapi/tla/verbs/asserted");
            verb.Display = new LanguageMap();
            verb.Display.Add("en-US", "asserted");

            var competencyIri = competency.IdNumber;
            if (string.IsNullOrEmpty(competencyIri) || !competencyIri.StartsWith("http"))
            {
                competencyIri = _xApiOptions.ApiUrl + "competencies/" + competency.Id;
            }

            var activity = new Activity();
            activity.Id = competencyIri;
            activity.Definition = new ActivityDefinition();
            activity.Definition.Type = new Uri("https://w3id.org/xapi/tla/activity-types/competency");
            activity.Definition.Name = new LanguageMap();
            activity.Definition.Name.Add("en-US", competency.ShortName ?? competency.IdNumber);
            activity.Definition.Description = new LanguageMap();
            activity.Definition.Description.Add("en-US", competency.Description ?? "");
            activity.Definition.Extensions = new TinCan.Extensions(
                new Newtonsoft.Json.Linq.JObject {
                    ["https://w3id.org/xapi/tla/extensions/competency-identifier"] = competency.IdNumber
                });

            var result = new TinCan.Result();
            result.Score = new TinCan.Score();
            result.Score.Raw = proficiencyLevel.Value;
            result.Score.Min = minValue;
            result.Score.Max = maxValue;
            if (maxValue > minValue)
                result.Score.Scaled = (double)(proficiencyLevel.Value - minValue) / (maxValue - minValue);
            result.Completion = true;
            if (!string.IsNullOrEmpty(assertion.Comment))
            {
                result.Response = assertion.Comment;
            }

            var context = new Context();
            context.Platform = _xApiContext.Platform;
            context.Language = "en-US";
            context.Registration = assertion.MselId;
            context.Extensions = new TinCan.Extensions(
                new Newtonsoft.Json.Linq.JObject {
                    ["https://w3id.org/xapi/tla/extensions/confidence"] = 1.0
                });

            if (assertion.TeamId.HasValue && assertion.TeamId.Value != Guid.Empty)
            {
                var team = _context.Teams.Find(assertion.TeamId.Value);
                if (team != null)
                {
                    var group = new TinCan.Group();
                    group.Name = team.ShortName;
                    if (!string.IsNullOrEmpty(_xApiOptions.EmailDomain))
                    {
                        group.Mbox = "mailto:" + team.ShortName + "@" + _xApiOptions.EmailDomain;
                    }
                    group.Account = new AgentAccount();
                    group.Account.HomePage = new Uri(_xApiOptions.UiUrl);
                    group.Account.Name = team.Id.ToString();
                    group.Member = new List<Agent> { _agent };
                    context.Team = group;
                }
            }

            var contextActivities = new ContextActivities();
            context.ContextActivities = contextActivities;

            // Parent: the MSEL
            contextActivities.Parent = new List<Activity>();
            var parentActivity = new Activity();
            parentActivity.Id = _xApiOptions.ApiUrl + "msel/" + msel.Id;
            parentActivity.Definition = new ActivityDefinition();
            parentActivity.Definition.Type = new Uri("http://adlnet.gov/expapi/activities/simulation");
            parentActivity.Definition.Name = new LanguageMap();
            parentActivity.Definition.Name.Add("en-US", msel.Name);
            contextActivities.Parent.Add(parentActivity);

            // Grouping: scenario event + move/group + integration IDs + framework
            contextActivities.Grouping = new List<Activity>();

            if (scenarioEvent != null)
            {
                var eventActivity = new Activity();
                eventActivity.Id = _xApiOptions.ApiUrl + "scenarioevents/" + scenarioEvent.Id;
                eventActivity.Definition = new ActivityDefinition();
                eventActivity.Definition.Type = new Uri("http://id.tincanapi.com/activitytype/step");
                contextActivities.Grouping.Add(eventActivity);
            }

            if (assertion.MoveNumber.HasValue)
            {
                var moveActivity = new Activity();
                moveActivity.Id = _xApiOptions.ApiUrl + "msels/" + msel.Id + "/moves/" + assertion.MoveNumber.Value;
                moveActivity.Definition = new ActivityDefinition();
                moveActivity.Definition.Type = new Uri("http://id.tincanapi.com/activitytype/phase");
                moveActivity.Definition.Name = new LanguageMap();
                moveActivity.Definition.Name.Add("en-US", "Move " + assertion.MoveNumber.Value);
                contextActivities.Grouping.Add(moveActivity);
            }

            if (assertion.GroupNumber.HasValue)
            {
                var groupActivity = new Activity();
                groupActivity.Id = _xApiOptions.ApiUrl + "msels/" + msel.Id + "/moves/" + (assertion.MoveNumber ?? 0) + "/groups/" + assertion.GroupNumber.Value;
                groupActivity.Definition = new ActivityDefinition();
                groupActivity.Definition.Type = new Uri("http://id.tincanapi.com/activitytype/group-assignment");
                groupActivity.Definition.Name = new LanguageMap();
                groupActivity.Definition.Name.Add("en-US", "Group " + assertion.GroupNumber.Value);
                contextActivities.Grouping.Add(groupActivity);
            }

            // Framework grouping (TLA competency-framework activity type)
            if (competency.CompetencyFramework != null)
            {
                var frameworkIri = competency.CompetencyFramework.IdNumber;
                if (string.IsNullOrEmpty(frameworkIri) || !frameworkIri.StartsWith("http"))
                    frameworkIri = _xApiOptions.ApiUrl + "competency-frameworks/" + competency.CompetencyFrameworkId;
                var frameworkActivity = new Activity();
                frameworkActivity.Id = frameworkIri;
                frameworkActivity.Definition = new ActivityDefinition();
                frameworkActivity.Definition.Type = new Uri("https://w3id.org/xapi/tla/activity-types/competency-framework");
                frameworkActivity.Definition.Name = new LanguageMap();
                frameworkActivity.Definition.Name.Add("en-US", competency.CompetencyFramework.Name);
                contextActivities.Grouping.Add(frameworkActivity);
            }

            // Integration groupings
            foreach (var ig in BuildIntegrationGroupings(msel))
            {
                if (ig.Count > 0)
                {
                    var apiUrl = ig.ContainsKey("apiUrl") ? ig["apiUrl"] : _xApiOptions.ApiUrl;
                    var igActivity = new Activity();
                    igActivity.Id = apiUrl + ig["type"] + "/" + ig["id"];
                    igActivity.Definition = new ActivityDefinition();
                    igActivity.Definition.Name = new LanguageMap();
                    igActivity.Definition.Name.Add("en-US", ig["name"]);
                    igActivity.Definition.Type = new Uri(ig["activityType"]);
                    contextActivities.Grouping.Add(igActivity);
                }
            }

            // Category: Crucible xAPI profile
            contextActivities.Category = new List<Activity>();
            var crucibleProfile = new Activity();
            crucibleProfile.Id = "https://crucible.sei.cmu.edu/xapi/profile/v1";
            crucibleProfile.Definition = new ActivityDefinition();
            crucibleProfile.Definition.Type = new Uri("http://adlnet.gov/expapi/activities/profile");
            crucibleProfile.Definition.Name = new LanguageMap();
            crucibleProfile.Definition.Name.Add("en-US", "Crucible xAPI Profile");
            contextActivities.Category.Add(crucibleProfile);

            var statement = new Statement();
            statement.Actor = _agent;
            statement.Verb = verb;
            statement.Target = activity;
            statement.Result = result;
            statement.Context = context;

            var statementJson = statement.ToJson();

            var queuedStatement = new XApiQueuedStatementEntity
            {
                Id = Guid.NewGuid(),
                StatementJson = statementJson,
                Verb = "asserted",
                ActivityId = activity.Id,
                MselId = assertion.MselId,
                TeamId = assertion.TeamId
            };

            await _queueService.EnqueueAsync(queuedStatement, ct);
            _logger.LogInformation("Enqueued competency assertion for {CompetencyId} on MSEL {MselId}", assertion.CompetencyId, assertion.MselId);

            return true;
        }

        public async Task<bool> RecordCheckboxChangeAsync(Guid mselId, Guid eventId, Guid dataFieldId, string dataFieldName, bool isChecked, CancellationToken ct)
        {
            if (!IsConfigured())
            {
                _logger.LogInformation("xAPI Service not configured");
                return true;
            }

            EnsureAgentInitialized();

            var msel = await _context.Msels.FirstOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new ArgumentException($"MSEL {mselId} not found");

            var scenarioEvent = await _context.ScenarioEvents.FirstOrDefaultAsync(se => se.Id == eventId, ct);
            if (scenarioEvent == null)
                throw new ArgumentException($"Scenario event {eventId} not found");

            var verb = new Verb();
            if (isChecked)
            {
                verb.Id = new Uri("https://w3id.org/xapi/dod-isd/verbs/selected");
                verb.Display = new LanguageMap();
                verb.Display.Add("en-US", "selected");
            }
            else
            {
                verb.Id = new Uri("https://w3id.org/xapi/dod-isd/verbs/reset");
                verb.Display = new LanguageMap();
                verb.Display.Add("en-US", "reset");
            }

            var activity = new Activity();
            activity.Id = _xApiOptions.ApiUrl + "scenarioevents/" + eventId + "/datafields/" + dataFieldId;
            activity.Definition = new ActivityDefinition();
            activity.Definition.Type = new Uri("http://id.tincanapi.com/activitytype/checklist-item");
            activity.Definition.Name = new LanguageMap();
            activity.Definition.Name.Add("en-US", dataFieldName);

            var result = new TinCan.Result();
            result.Completion = isChecked;
            result.Success = isChecked;

            var context = new Context();
            context.Platform = _xApiContext.Platform;
            context.Language = "en-US";
            context.Registration = mselId;

            // Build context activities
            var contextActivities = new ContextActivities();

            // Parent: MSEL
            contextActivities.Parent = new List<Activity>();
            var mselActivity = new Activity();
            mselActivity.Id = _xApiOptions.ApiUrl + "msels/" + msel.Id;
            mselActivity.Definition = new ActivityDefinition();
            mselActivity.Definition.Type = new Uri("http://adlnet.gov/expapi/activities/simulation");
            mselActivity.Definition.Name = new LanguageMap();
            mselActivity.Definition.Name.Add("en-US", msel.Name);
            contextActivities.Parent.Add(mselActivity);

            // Grouping: Event
            contextActivities.Grouping = new List<Activity>();
            var eventActivity = new Activity();
            eventActivity.Id = _xApiOptions.ApiUrl + "scenarioevents/" + scenarioEvent.Id;
            eventActivity.Definition = new ActivityDefinition();
            eventActivity.Definition.Type = new Uri("http://id.tincanapi.com/activitytype/step");
            eventActivity.Definition.Name = new LanguageMap();
            eventActivity.Definition.Name.Add("en-US", "Scenario Event");
            contextActivities.Grouping.Add(eventActivity);

            // Move grouping - find which move this event belongs to by comparing DeltaSeconds
            var move = await _context.Moves
                .Where(m => m.MselId == mselId && m.DeltaSeconds <= scenarioEvent.DeltaSeconds)
                .OrderByDescending(m => m.DeltaSeconds)
                .FirstOrDefaultAsync(ct);
            if (move != null)
            {
                var moveActivity = new Activity();
                moveActivity.Id = _xApiOptions.ApiUrl + "moves/" + move.Id;
                moveActivity.Definition = new ActivityDefinition();
                moveActivity.Definition.Type = new Uri("http://id.tincanapi.com/activitytype/collection-simple");
                moveActivity.Definition.Name = new LanguageMap();
                moveActivity.Definition.Name.Add("en-US", move.Description ?? "Move " + move.MoveNumber);
                contextActivities.Grouping.Add(moveActivity);
            }

            // Integration groupings
            foreach (var ig in BuildIntegrationGroupings(msel))
            {
                if (ig.Count > 0)
                {
                    var apiUrl = ig.ContainsKey("apiUrl") ? ig["apiUrl"] : _xApiOptions.ApiUrl;
                    var igActivity = new Activity();
                    igActivity.Id = apiUrl + ig["type"] + "/" + ig["id"];
                    igActivity.Definition = new ActivityDefinition();
                    igActivity.Definition.Name = new LanguageMap();
                    igActivity.Definition.Name.Add("en-US", ig["name"]);
                    igActivity.Definition.Type = new Uri(ig["activityType"]);
                    contextActivities.Grouping.Add(igActivity);
                }
            }

            // Category: Crucible xAPI profile
            contextActivities.Category = new List<Activity>();
            var crucibleProfile = new Activity();
            crucibleProfile.Id = "https://crucible.sei.cmu.edu/xapi/profile/v1";
            crucibleProfile.Definition = new ActivityDefinition();
            crucibleProfile.Definition.Type = new Uri("http://adlnet.gov/expapi/activities/profile");
            crucibleProfile.Definition.Name = new LanguageMap();
            crucibleProfile.Definition.Name.Add("en-US", "Crucible xAPI Profile");
            contextActivities.Category.Add(crucibleProfile);

            context.ContextActivities = contextActivities;

            var statement = new Statement();
            statement.Actor = _agent;
            statement.Verb = verb;
            statement.Target = activity;
            statement.Result = result;
            statement.Context = context;

            var statementJson = statement.ToJson();

            var queuedStatement = new XApiQueuedStatementEntity
            {
                Id = Guid.NewGuid(),
                StatementJson = statementJson,
                Verb = "completed",
                ActivityId = activity.Id,
                MselId = mselId,
                TeamId = null
            };

            await _queueService.EnqueueAsync(queuedStatement, ct);
            _logger.LogInformation("Enqueued checkbox change for field {DataFieldId} on event {EventId}, checked={IsChecked}", dataFieldId, eventId, isChecked);

            return true;
        }

        public async Task<string> GetStatementsAsync(Guid mselId, DateTime? since, DateTime? until, int limit, string source, bool hasSystemPermission, CancellationToken ct)
        {
            if (!IsConfigured())
            {
                return "{\"statements\":[]}";
            }

            var msel = await _context.Msels.FirstOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
            {
                return "{\"statements\":[]}";
            }

            // the same MSEL roles that can open the Assessor View
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context) &&
                !await MselEditorRequirement.IsMet(_user.GetId(), msel.Id, _context) &&
                !await MselApproverRequirement.IsMet(_user.GetId(), msel.Id, _context) &&
                !await EvaluatorRequirement.IsMet(_user.GetId(), msel.Id, _context))
                throw new ForbiddenException();

            // the MSEL's latest run holds the ids even after its integrations were removed, which clears the
            // MSEL's own; the MSEL's columns stand in for an id the run lacks, or for a MSEL with no run
            var run = await IntegrationRunExtensions.FindLatestRunAsync(_context, msel.Id, ct);
            var registrationIds = BuildRegistrationIds(msel.Id, IntegrationRunExtensions.WithMselIds(run, msel), source);
            if (registrationIds.Count == 0)
            {
                return "{\"statements\":[]}";
            }

            using var httpClient = new HttpClient();
            var credentials = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_xApiOptions.Username}:{_xApiOptions.Password}"));
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {credentials}");
            httpClient.DefaultRequestHeaders.Add("X-Experience-API-Version", "1.0.3");

            var allStatements = new List<string>();
            foreach (var registrationId in registrationIds)
            {
                var queryParams = new List<string>();
                queryParams.Add($"registration={registrationId}");
                queryParams.Add($"limit={limit}");
                if (since.HasValue)
                    queryParams.Add($"since={since.Value:O}");
                if (until.HasValue)
                    queryParams.Add($"until={until.Value:O}");

                var url = $"{_xApiOptions.Endpoint}/statements?{string.Join("&", queryParams)}";
                allStatements.AddRange(await GetStatementPagesAsync(httpClient, registrationId, url, ct));
            }

            var merged = MergeStatementResponses(allStatements);

            // The MSEL's owners and anyone who can view every MSEL keep the names; they run the event.
            if (msel.HideNamesOnAssessorView &&
                !hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(_user.GetId(), msel.Id, _context))
            {
                return WithoutNames(merged);
            }

            return merged;
        }

        /// <summary>
        /// Every page of one registration's statements: the first query, then each <c>more</c> link until the
        /// LRS sends an empty one or none, a link repeats, or <c>MaxStatementsPerRegistration</c> is reached.
        /// A page the LRS refuses ends the paging with the pages already read; an LRS that cannot be reached
        /// on the first page throws, as it always has, and on a later page keeps the pages already read.
        /// </summary>
        private async Task<List<string>> GetStatementPagesAsync(HttpClient httpClient, string registrationId, string firstUrl, CancellationToken ct)
        {
            var pages = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var cap = _xApiOptions.MaxStatementsPerRegistration > 0
                ? _xApiOptions.MaxStatementsPerRegistration
                : XApiOptions.DefaultMaxStatementsPerRegistration;
            var count = 0;
            var url = firstUrl;

            while (url != null)
            {
                visited.Add(url);

                HttpResponseMessage response;
                try
                {
                    response = await httpClient.GetAsync(url, ct);
                }
                catch (HttpRequestException ex) when (pages.Count > 0)
                {
                    _logger.LogWarning(ex, "Failed to read the next page of LRS statements for registration {RegistrationId} after {Count} statements", registrationId, count);
                    break;
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to query LRS for registration {RegistrationId}: HTTP {StatusCode}", registrationId, response.StatusCode);
                        break;
                    }

                    string json;
                    try
                    {
                        json = await response.Content.ReadAsStringAsync(ct);
                    }
                    catch (Exception ex) when ((ex is HttpRequestException || ex is System.IO.IOException) && pages.Count > 0)
                    {
                        _logger.LogWarning(ex, "Failed to read the next page of LRS statements for registration {RegistrationId} after {Count} statements", registrationId, count);
                        break;
                    }

                    var (isStatementList, statementCount, more) = ReadPage(json);
                    if (!isStatementList)
                    {
                        _logger.LogWarning("Ignored an LRS answer that is not a statement list for registration {RegistrationId} after {Count} statements", registrationId, count);
                        break;
                    }

                    pages.Add(json);
                    count += statementCount;

                    if (string.IsNullOrWhiteSpace(more))
                        break;

                    if (count >= cap)
                    {
                        _logger.LogWarning("Stopped reading LRS statements for registration {RegistrationId} at {Count} statements, the MaxStatementsPerRegistration limit of {Cap}", registrationId, count, cap);
                        break;
                    }

                    var next = ResolveMore(_xApiOptions.Endpoint, more);
                    if (next == null)
                    {
                        _logger.LogWarning("Ignored an LRS more link that is not a url on the LRS for registration {RegistrationId}: {More}", registrationId, more);
                        break;
                    }

                    if (visited.Contains(next))
                    {
                        _logger.LogWarning("Stopped reading LRS statements for registration {RegistrationId} at {Count} statements: the LRS repeated the more link {More}", registrationId, count, more);
                        break;
                    }

                    url = next;
                }
            }

            return pages;
        }

        /// <summary>Whether a page is a statement list, how many statements it holds, and its <c>more</c> link, if it has one.</summary>
        private static (bool IsStatementList, int Count, string More) ReadPage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                    return (true, root.GetArrayLength(), null);

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("statements", out var statements) ||
                    statements.ValueKind != JsonValueKind.Array)
                    return (false, 0, null);

                var count = statements.GetArrayLength();
                var more = root.TryGetProperty("more", out var moreProp) && moreProp.ValueKind == JsonValueKind.String
                    ? moreProp.GetString()
                    : null;
                return (true, count, more);
            }
            catch (JsonException)
            {
                return (false, 0, null);
            }
        }

        /// <summary>
        /// The url a <c>more</c> link points to. xAPI 1.0.3 makes it an IRL, which an LRS usually writes
        /// relative to its host (<c>/xapi/statements?...</c>), so that is resolved against the scheme, host
        /// and port of <c>Endpoint</c>; an absolute http(s) url is used as it is if it is on that same scheme, host
        /// and port, and is refused (null) anywhere else. A path with no leading slash
        /// is taken as relative to <c>Endpoint</c> itself.
        /// </summary>
        internal static string ResolveMore(string endpoint, string more)
        {
            more = more?.Trim();
            if (string.IsNullOrEmpty(more))
                return null;

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
                return null;

            // On Linux "/xapi/statements" parses as an absolute file:// uri, so the scheme is what decides.
            // An absolute link is followed only on the LRS's own scheme, host and port: every page is sent
            // the LRS credentials, so a link elsewhere would hand them to whoever it names.
            if (Uri.TryCreate(more, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                return Uri.Compare(absolute, baseUri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
                    ? absolute.AbsoluteUri
                    : null;

            if (!more.StartsWith('/'))
                baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");

            return Uri.TryCreate(baseUri, more, out var resolved) ? resolved.AbsoluteUri : null;
        }

        /// <summary>The properties that say which person an agent is: its name and every inverse functional identifier.</summary>
        private static readonly string[] AgentIdentity = ["name", "mbox", "mbox_sha1sum", "openid", "account"];

        /// <summary>
        /// A statement list with nothing left in it that says which person a statement is about, for the
        /// Assessor View of a MSEL that hides names. An agent actor or object loses its name and identifiers,
        /// a group keeps its own (that is the team) but loses its members, and <c>context.team</c> loses its
        /// members. <c>context.instructor</c> is the assessor, so it is kept. The LRS is not changed.
        /// </summary>
        internal static string WithoutNames(string json)
        {
            var root = JsonNode.Parse(json);
            var statements = root as JsonArray ?? root?["statements"] as JsonArray;
            if (statements == null)
                return json;

            foreach (var statement in statements.OfType<JsonObject>())
                RemoveNames(statement);

            return root.ToJsonString();
        }

        private static void RemoveNames(JsonObject statement)
        {
            RemoveIdentity(statement["actor"] as JsonObject);

            if (statement["object"] is JsonObject obj)
            {
                var type = ObjectType(obj);
                if (type == "Agent" || type == "Group")
                    RemoveIdentity(obj);
                else if (type == "SubStatement")
                    RemoveNames(obj);
            }

            if (statement["context"] is not JsonObject context)
                return;

            if (context["team"] is JsonObject team)
                team.Remove("member");

            // CITE names the person a statement is about with a user-profile activity in
            // contextActivities (its id ends in the user's id), so those go too.
            if (context["contextActivities"] is JsonObject contextActivities)
            {
                foreach (var (_, list) in contextActivities)
                {
                    if (list is not JsonArray activities)
                        continue;

                    foreach (var activity in activities.OfType<JsonObject>().Where(IsUserProfile).ToList())
                        activities.Remove(activity);
                }
            }
        }

        private const string UserProfileActivityType = "http://id.tincanapi.com/activitytype/user-profile";

        private static bool IsUserProfile(JsonObject activity) =>
            activity["definition"] is JsonObject definition &&
            definition["type"] is JsonValue value &&
            value.TryGetValue<string>(out var type) &&
            type == UserProfileActivityType;

        private static void RemoveIdentity(JsonObject agent)
        {
            if (agent == null)
                return;

            if (ObjectType(agent) == "Group")
            {
                agent.Remove("member");
                return;
            }

            foreach (var property in AgentIdentity)
                agent.Remove(property);
        }

        private static string ObjectType(JsonObject node) =>
            node["objectType"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : null;

        private static List<string> BuildRegistrationIds(Guid mselId, MselRunEntity ids, string source)
        {
            var registrationIds = new List<string>();
            var filterBySource = !string.IsNullOrWhiteSpace(source);

            if (!filterBySource || source.Equals("blueprint", StringComparison.OrdinalIgnoreCase))
            {
                registrationIds.Add(mselId.ToString());
            }

            if ((!filterBySource || source.Equals("cite", StringComparison.OrdinalIgnoreCase))
                && ids.CiteEvaluationId.HasValue)
            {
                registrationIds.Add(ids.CiteEvaluationId.Value.ToString());
            }

            if ((!filterBySource || source.Equals("steamfitter", StringComparison.OrdinalIgnoreCase))
                && ids.SteamfitterScenarioId.HasValue)
            {
                registrationIds.Add(ids.SteamfitterScenarioId.Value.ToString());
            }

            if ((!filterBySource || source.Equals("player", StringComparison.OrdinalIgnoreCase))
                && ids.PlayerViewId.HasValue)
            {
                registrationIds.Add(ids.PlayerViewId.Value.ToString());
            }

            if ((!filterBySource || source.Equals("gallery", StringComparison.OrdinalIgnoreCase))
                && ids.GalleryExhibitId.HasValue)
            {
                registrationIds.Add(ids.GalleryExhibitId.Value.ToString());
            }

            return registrationIds;
        }

        private string MergeStatementResponses(List<string> responses)
        {
            if (responses.Count == 0)
                return "{\"statements\":[]}";

            if (responses.Count == 1)
                return responses[0];

            var seen = new HashSet<string>();
            var merged = new List<JsonElement>();
            foreach (var json in responses)
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement statementsArray;
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("statements", out statementsArray))
                {
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    statementsArray = doc.RootElement;
                }
                else
                {
                    continue;
                }

                foreach (var stmt in statementsArray.EnumerateArray())
                {
                    var id = stmt.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    if (id != null && !seen.Add(id))
                        continue;
                    merged.Add(stmt.Clone());
                }
            }

            merged.Sort((a, b) =>
            {
                var tsA = a.TryGetProperty("timestamp", out var tA) ? tA.GetString() : "";
                var tsB = b.TryGetProperty("timestamp", out var tB) ? tB.GetString() : "";
                return string.Compare(tsB, tsA, StringComparison.Ordinal);
            });

            using var ms = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("statements");
                foreach (var stmt in merged)
                    stmt.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        private List<Dictionary<string, string>> BuildIntegrationGroupings(MselEntity msel)
        {
            var grouping = new List<Dictionary<string, string>>();

            if (msel.PlayerViewId.HasValue && !string.IsNullOrEmpty(_clientOptions.PlayerApiUrl))
            {
                var playerApiUrl = _clientOptions.PlayerApiUrl.EndsWith("/")
                    ? _clientOptions.PlayerApiUrl + "api/"
                    : _clientOptions.PlayerApiUrl + "/api/";

                var playerIntegration = new Dictionary<string, string>();
                playerIntegration.Add("id", msel.PlayerViewId.Value.ToString());
                playerIntegration.Add("name", "Player View");
                playerIntegration.Add("description", "Player application view for " + msel.Name);
                playerIntegration.Add("type", "views");
                playerIntegration.Add("activityType", "http://id.tincanapi.com/activitytype/resource");
                playerIntegration.Add("moreInfo", "");
                playerIntegration.Add("apiUrl", playerApiUrl);
                grouping.Add(playerIntegration);
            }

            if (msel.GalleryExhibitId.HasValue && !string.IsNullOrEmpty(_clientOptions.GalleryApiUrl))
            {
                var galleryApiUrl = _clientOptions.GalleryApiUrl.EndsWith("/")
                    ? _clientOptions.GalleryApiUrl + "api/"
                    : _clientOptions.GalleryApiUrl + "/api/";

                var galleryIntegration = new Dictionary<string, string>();
                galleryIntegration.Add("id", msel.GalleryExhibitId.Value.ToString());
                galleryIntegration.Add("name", "Gallery Exhibit");
                galleryIntegration.Add("description", "Gallery exhibit for " + msel.Name);
                galleryIntegration.Add("type", "exhibits");
                galleryIntegration.Add("activityType", "http://id.tincanapi.com/activitytype/resource");
                galleryIntegration.Add("moreInfo", "");
                galleryIntegration.Add("apiUrl", galleryApiUrl);
                grouping.Add(galleryIntegration);
            }

            if (msel.CiteEvaluationId.HasValue && !string.IsNullOrEmpty(_clientOptions.CiteApiUrl))
            {
                var citeApiUrl = _clientOptions.CiteApiUrl.EndsWith("/")
                    ? _clientOptions.CiteApiUrl + "api/"
                    : _clientOptions.CiteApiUrl + "/api/";

                var citeIntegration = new Dictionary<string, string>();
                citeIntegration.Add("id", msel.CiteEvaluationId.Value.ToString());
                citeIntegration.Add("name", "CITE Evaluation");
                citeIntegration.Add("description", "CITE evaluation for " + msel.Name);
                citeIntegration.Add("type", "evaluations");
                citeIntegration.Add("activityType", "http://id.tincanapi.com/activitytype/resource");
                citeIntegration.Add("moreInfo", "");
                citeIntegration.Add("apiUrl", citeApiUrl);
                grouping.Add(citeIntegration);
            }

            if (msel.SteamfitterScenarioId.HasValue && !string.IsNullOrEmpty(_clientOptions.SteamfitterApiUrl))
            {
                var steamfitterApiUrl = _clientOptions.SteamfitterApiUrl.EndsWith("/")
                    ? _clientOptions.SteamfitterApiUrl + "api/"
                    : _clientOptions.SteamfitterApiUrl + "/api/";

                var steamfitterIntegration = new Dictionary<string, string>();
                steamfitterIntegration.Add("id", msel.SteamfitterScenarioId.Value.ToString());
                steamfitterIntegration.Add("name", "Steamfitter Scenario");
                steamfitterIntegration.Add("description", "Steamfitter scenario for " + msel.Name);
                steamfitterIntegration.Add("type", "scenarios");
                steamfitterIntegration.Add("activityType", "http://id.tincanapi.com/activitytype/resource");
                steamfitterIntegration.Add("moreInfo", "");
                steamfitterIntegration.Add("apiUrl", steamfitterApiUrl);
                grouping.Add(steamfitterIntegration);
            }

            return grouping;
        }
    }
}
