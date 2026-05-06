// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
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
        Task<bool> MselViewedAsync(Guid id, CancellationToken ct);
        Task<bool> ExerciseStartedAsync(MselEntity msel, CancellationToken ct);
        Task<bool> ExerciseStoppedAsync(MselEntity msel, CancellationToken ct);
        Task<bool> JoinPageViewedAsync(CancellationToken ct);
        Task<bool> MselJoinedAsync(MselEntity msel, Guid? teamId, CancellationToken ct);
        Task<string> GetStatementsAsync(Guid mselId, DateTime? since, DateTime? until, int limit, string source, CancellationToken ct);
        Task<bool> AssertCompetencyAsync(ViewModels.CompetencyAssertion assertion, CancellationToken ct);
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
            _agent.name = _context.Users.Where(u => u.Id == _user.GetId()).Select(u => u.Name).FirstOrDefault();
            _agent.account = _account;

            // Initialize the Context
            _xApiContext = new Context();
            _xApiContext.platform = _xApiOptions.Platform;
            _xApiContext.language = "en-US";
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
                        // Use apiUrl from groupingItem if available, otherwise use Blueprint API URL
                        var apiUrl = groupingItem.ContainsKey("apiUrl") ? groupingItem["apiUrl"] : _xApiOptions.ApiUrl;
                        grouping.id = apiUrl + groupingItem["type"] + "/" + groupingItem["id"];
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
            var grouping = BuildIntegrationGroupings(msel);
            var other = new Dictionary<string, string>();
            var teamId = await GetUserTeamIdAsync(msel.Id, ct);

            return await CreateAsync(verb, activity, category, grouping, parent, other, msel.Id, teamId, ct);
        }

        public async Task<bool> MselViewedAsync(Guid id, CancellationToken ct)
        {
            var msel = await _context.Msels.FindAsync(id, ct);
            if (msel == null)
            {
                return false;
            }
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

        public async Task<bool> MselJoinedAsync(MselEntity msel, Guid? teamId, CancellationToken ct)
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

            return await CreateAsync(verb, activity, category, grouping, parent, other, msel.Id, teamId, ct);
        }

        private async Task<Guid?> GetUserTeamIdAsync(Guid mselId, CancellationToken ct)
        {
            return await _context.TeamUsers
                .Where(tu => tu.UserId == _user.GetId() && tu.Team.MselId == mselId)
                .Select(tu => (Guid?)tu.TeamId)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<bool> AssertCompetencyAsync(ViewModels.CompetencyAssertion assertion, CancellationToken ct)
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
                scenarioEvent = await _context.ScenarioEvents.FirstOrDefaultAsync(se => se.Id == assertion.ScenarioEventId.Value, ct);
                if (scenarioEvent == null)
                    throw new ArgumentException($"Scenario event {assertion.ScenarioEventId} not found");
            }

            var scale = proficiencyLevel.ProficiencyScale;
            var allLevels = await _context.ProficiencyLevels
                .Where(pl => pl.ProficiencyScaleId == scale.Id)
                .ToListAsync(ct);
            var minValue = allLevels.Min(pl => pl.Value);
            var maxValue = allLevels.Max(pl => pl.Value);

            var verb = new Verb();
            verb.id = new Uri("https://w3id.org/xapi/tla/verbs/asserted");
            verb.display = new LanguageMap();
            verb.display.Add("en-US", "asserted");

            var competencyIri = competency.IdNumber;
            if (string.IsNullOrEmpty(competencyIri) || !competencyIri.StartsWith("http"))
            {
                competencyIri = _xApiOptions.ApiUrl + "competencies/" + competency.Id;
            }

            var activity = new Activity();
            activity.id = competencyIri;
            activity.definition = new ActivityDefinition();
            activity.definition.type = new Uri("https://w3id.org/xapi/tla/activity-types/competency");
            activity.definition.name = new LanguageMap();
            activity.definition.name.Add("en-US", competency.ShortName ?? competency.IdNumber);
            activity.definition.description = new LanguageMap();
            activity.definition.description.Add("en-US", competency.Description ?? "");
            activity.definition.extensions = new TinCan.Extensions(
                new Newtonsoft.Json.Linq.JObject {
                    ["https://w3id.org/xapi/tla/extensions/competency-identifier"] = competency.IdNumber
                });

            var result = new TinCan.Result();
            result.score = new TinCan.Score();
            result.score.raw = proficiencyLevel.Value;
            result.score.min = minValue;
            result.score.max = maxValue;
            if (maxValue > minValue)
                result.score.scaled = (double)(proficiencyLevel.Value - minValue) / (maxValue - minValue);
            result.completion = true;
            if (!string.IsNullOrEmpty(assertion.Comment))
            {
                result.response = assertion.Comment;
            }

            var context = new Context();
            context.platform = _xApiContext.platform;
            context.language = "en-US";
            context.registration = assertion.MselId;
            context.extensions = new TinCan.Extensions(
                new Newtonsoft.Json.Linq.JObject {
                    ["https://w3id.org/xapi/tla/extensions/confidence"] = 1.0
                });

            if (assertion.TeamId.HasValue && assertion.TeamId.Value != Guid.Empty)
            {
                var team = _context.Teams.Find(assertion.TeamId.Value);
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

            // Parent: the MSEL
            contextActivities.parent = new List<Activity>();
            var parentActivity = new Activity();
            parentActivity.id = _xApiOptions.ApiUrl + "msel/" + msel.Id;
            parentActivity.definition = new ActivityDefinition();
            parentActivity.definition.type = new Uri("http://adlnet.gov/expapi/activities/simulation");
            parentActivity.definition.name = new LanguageMap();
            parentActivity.definition.name.Add("en-US", msel.Name);
            contextActivities.parent.Add(parentActivity);

            // Grouping: scenario event + move/group + integration IDs + framework
            contextActivities.grouping = new List<Activity>();

            if (scenarioEvent != null)
            {
                var eventActivity = new Activity();
                eventActivity.id = _xApiOptions.ApiUrl + "scenarioevents/" + scenarioEvent.Id;
                eventActivity.definition = new ActivityDefinition();
                eventActivity.definition.type = new Uri("http://id.tincanapi.com/activitytype/step");
                contextActivities.grouping.Add(eventActivity);
            }

            if (assertion.MoveNumber.HasValue)
            {
                var moveActivity = new Activity();
                moveActivity.id = _xApiOptions.ApiUrl + "msels/" + msel.Id + "/moves/" + assertion.MoveNumber.Value;
                moveActivity.definition = new ActivityDefinition();
                moveActivity.definition.type = new Uri("http://id.tincanapi.com/activitytype/phase");
                moveActivity.definition.name = new LanguageMap();
                moveActivity.definition.name.Add("en-US", "Move " + assertion.MoveNumber.Value);
                contextActivities.grouping.Add(moveActivity);
            }

            if (assertion.GroupNumber.HasValue)
            {
                var groupActivity = new Activity();
                groupActivity.id = _xApiOptions.ApiUrl + "msels/" + msel.Id + "/moves/" + (assertion.MoveNumber ?? 0) + "/groups/" + assertion.GroupNumber.Value;
                groupActivity.definition = new ActivityDefinition();
                groupActivity.definition.type = new Uri("http://id.tincanapi.com/activitytype/group-assignment");
                groupActivity.definition.name = new LanguageMap();
                groupActivity.definition.name.Add("en-US", "Group " + assertion.GroupNumber.Value);
                contextActivities.grouping.Add(groupActivity);
            }

            // Framework grouping (TLA competency-framework activity type)
            if (competency.CompetencyFramework != null)
            {
                var frameworkIri = competency.CompetencyFramework.IdNumber;
                if (string.IsNullOrEmpty(frameworkIri) || !frameworkIri.StartsWith("http"))
                    frameworkIri = _xApiOptions.ApiUrl + "competency-frameworks/" + competency.CompetencyFrameworkId;
                var frameworkActivity = new Activity();
                frameworkActivity.id = frameworkIri;
                frameworkActivity.definition = new ActivityDefinition();
                frameworkActivity.definition.type = new Uri("https://w3id.org/xapi/tla/activity-types/competency-framework");
                frameworkActivity.definition.name = new LanguageMap();
                frameworkActivity.definition.name.Add("en-US", competency.CompetencyFramework.Name);
                contextActivities.grouping.Add(frameworkActivity);
            }

            // Integration groupings
            foreach (var ig in BuildIntegrationGroupings(msel))
            {
                if (ig.Count > 0)
                {
                    var apiUrl = ig.ContainsKey("apiUrl") ? ig["apiUrl"] : _xApiOptions.ApiUrl;
                    var igActivity = new Activity();
                    igActivity.id = apiUrl + ig["type"] + "/" + ig["id"];
                    igActivity.definition = new ActivityDefinition();
                    igActivity.definition.name = new LanguageMap();
                    igActivity.definition.name.Add("en-US", ig["name"]);
                    igActivity.definition.type = new Uri(ig["activityType"]);
                    contextActivities.grouping.Add(igActivity);
                }
            }

            // Category: Crucible xAPI profile
            contextActivities.category = new List<Activity>();
            var crucibleProfile = new Activity();
            crucibleProfile.id = "https://crucible.sei.cmu.edu/xapi/profile/v1";
            crucibleProfile.definition = new ActivityDefinition();
            crucibleProfile.definition.type = new Uri("http://adlnet.gov/expapi/activities/profile");
            crucibleProfile.definition.name = new LanguageMap();
            crucibleProfile.definition.name.Add("en-US", "Crucible xAPI Profile");
            contextActivities.category.Add(crucibleProfile);

            var statement = new Statement();
            statement.actor = _agent;
            statement.verb = verb;
            statement.target = activity;
            statement.result = result;
            statement.context = context;

            var statementJson = statement.ToJSON();

            var queuedStatement = new XApiQueuedStatementEntity
            {
                Id = Guid.NewGuid(),
                StatementJson = statementJson,
                Verb = "asserted",
                ActivityId = activity.id,
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
                verb.id = new Uri("https://w3id.org/xapi/dod-isd/verbs/selected");
                verb.display = new LanguageMap();
                verb.display.Add("en-US", "selected");
            }
            else
            {
                verb.id = new Uri("https://w3id.org/xapi/dod-isd/verbs/reset");
                verb.display = new LanguageMap();
                verb.display.Add("en-US", "reset");
            }

            var activity = new Activity();
            activity.id = _xApiOptions.ApiUrl + "scenarioevents/" + eventId + "/datafields/" + dataFieldId;
            activity.definition = new ActivityDefinition();
            activity.definition.type = new Uri("http://id.tincanapi.com/activitytype/checklist-item");
            activity.definition.name = new LanguageMap();
            activity.definition.name.Add("en-US", dataFieldName);

            var result = new TinCan.Result();
            result.completion = isChecked;
            result.success = isChecked;

            var context = new Context();
            context.platform = _xApiContext.platform;
            context.language = "en-US";
            context.registration = mselId;

            // Build context activities
            var contextActivities = new ContextActivities();

            // Parent: MSEL
            contextActivities.parent = new List<Activity>();
            var mselActivity = new Activity();
            mselActivity.id = _xApiOptions.ApiUrl + "msels/" + msel.Id;
            mselActivity.definition = new ActivityDefinition();
            mselActivity.definition.type = new Uri("http://adlnet.gov/expapi/activities/simulation");
            mselActivity.definition.name = new LanguageMap();
            mselActivity.definition.name.Add("en-US", msel.Name);
            contextActivities.parent.Add(mselActivity);

            // Grouping: Event
            contextActivities.grouping = new List<Activity>();
            var eventActivity = new Activity();
            eventActivity.id = _xApiOptions.ApiUrl + "scenarioevents/" + scenarioEvent.Id;
            eventActivity.definition = new ActivityDefinition();
            eventActivity.definition.type = new Uri("http://id.tincanapi.com/activitytype/step");
            eventActivity.definition.name = new LanguageMap();
            eventActivity.definition.name.Add("en-US", "Scenario Event");
            contextActivities.grouping.Add(eventActivity);

            // Move grouping - find which move this event belongs to by comparing DeltaSeconds
            var move = await _context.Moves
                .Where(m => m.MselId == mselId && m.DeltaSeconds <= scenarioEvent.DeltaSeconds)
                .OrderByDescending(m => m.DeltaSeconds)
                .FirstOrDefaultAsync(ct);
            if (move != null)
            {
                var moveActivity = new Activity();
                moveActivity.id = _xApiOptions.ApiUrl + "moves/" + move.Id;
                moveActivity.definition = new ActivityDefinition();
                moveActivity.definition.type = new Uri("http://id.tincanapi.com/activitytype/collection-simple");
                moveActivity.definition.name = new LanguageMap();
                moveActivity.definition.name.Add("en-US", move.Description ?? "Move " + move.MoveNumber);
                contextActivities.grouping.Add(moveActivity);
            }

            // Integration groupings
            foreach (var ig in BuildIntegrationGroupings(msel))
            {
                if (ig.Count > 0)
                {
                    var apiUrl = ig.ContainsKey("apiUrl") ? ig["apiUrl"] : _xApiOptions.ApiUrl;
                    var igActivity = new Activity();
                    igActivity.id = apiUrl + ig["type"] + "/" + ig["id"];
                    igActivity.definition = new ActivityDefinition();
                    igActivity.definition.name = new LanguageMap();
                    igActivity.definition.name.Add("en-US", ig["name"]);
                    igActivity.definition.type = new Uri(ig["activityType"]);
                    contextActivities.grouping.Add(igActivity);
                }
            }

            // Category: Crucible xAPI profile
            contextActivities.category = new List<Activity>();
            var crucibleProfile = new Activity();
            crucibleProfile.id = "https://crucible.sei.cmu.edu/xapi/profile/v1";
            crucibleProfile.definition = new ActivityDefinition();
            crucibleProfile.definition.type = new Uri("http://adlnet.gov/expapi/activities/profile");
            crucibleProfile.definition.name = new LanguageMap();
            crucibleProfile.definition.name.Add("en-US", "Crucible xAPI Profile");
            contextActivities.category.Add(crucibleProfile);

            context.contextActivities = contextActivities;

            var statement = new Statement();
            statement.actor = _agent;
            statement.verb = verb;
            statement.target = activity;
            statement.result = result;
            statement.context = context;

            var statementJson = statement.ToJSON();

            var queuedStatement = new XApiQueuedStatementEntity
            {
                Id = Guid.NewGuid(),
                StatementJson = statementJson,
                Verb = "completed",
                ActivityId = activity.id,
                MselId = mselId,
                TeamId = null
            };

            await _queueService.EnqueueAsync(queuedStatement, ct);
            _logger.LogInformation("Enqueued checkbox change for field {DataFieldId} on event {EventId}, checked={IsChecked}", dataFieldId, eventId, isChecked);

            return true;
        }

        public async Task<string> GetStatementsAsync(Guid mselId, DateTime? since, DateTime? until, int limit, string source, CancellationToken ct)
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

            var registrationIds = BuildRegistrationIds(msel, source);
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
                var response = await httpClient.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to query LRS for registration {RegistrationId}: HTTP {StatusCode}", registrationId, response.StatusCode);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                allStatements.Add(json);
            }

            return MergeStatementResponses(allStatements);
        }

        private List<string> BuildRegistrationIds(MselEntity msel, string source)
        {
            var registrationIds = new List<string>();
            var filterBySource = !string.IsNullOrWhiteSpace(source);

            if (!filterBySource || source.Equals("blueprint", StringComparison.OrdinalIgnoreCase))
            {
                registrationIds.Add(msel.Id.ToString());
            }

            if ((!filterBySource || source.Equals("cite", StringComparison.OrdinalIgnoreCase))
                && msel.CiteEvaluationId.HasValue)
            {
                registrationIds.Add(msel.CiteEvaluationId.Value.ToString());
            }

            if ((!filterBySource || source.Equals("steamfitter", StringComparison.OrdinalIgnoreCase))
                && msel.SteamfitterScenarioId.HasValue)
            {
                registrationIds.Add(msel.SteamfitterScenarioId.Value.ToString());
            }

            if ((!filterBySource || source.Equals("player", StringComparison.OrdinalIgnoreCase))
                && msel.PlayerViewId.HasValue)
            {
                registrationIds.Add(msel.PlayerViewId.Value.ToString());
            }

            if ((!filterBySource || source.Equals("gallery", StringComparison.OrdinalIgnoreCase))
                && msel.GalleryExhibitId.HasValue)
            {
                registrationIds.Add(msel.GalleryExhibitId.Value.ToString());
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
