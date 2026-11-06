// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Builds a MSEL from a reviewed AI draft in three stages, so it can be built all at once (a draft imported whole) or
    /// stage by stage into a MSEL that already exists (a draft built into its MSEL as it is written):
    /// the shell (settings, teams, organizations, cards, moves, competencies), the injects (rows, CITE tasks, the
    /// read-in), and the finish (the overview, facilitator and profile pages, and the Player applications).
    /// Each stage works on the MSEL's entity graph; the caller saves it.
    /// </summary>
    public static class MselDraftBuilder
    {
        /// <summary>The MSEL's own settings and everything the injects hang off; the template's rows and moves go</summary>
        public static void PrepareShell(MselEntity mselEntity, MselDraft draft, IReadOnlyDictionary<string, CompetencyEntity> competencies)
        {
            mselEntity.Name = draft.Name;
            // within what the MSEL's form allows, whatever path the draft came by
            mselEntity.Description = draft.Description?.Trim().Length > MselDraftRules.MaxDescription
                ? MselImageService.Shorten(draft.Description, MselDraftRules.MaxDescription)
                : draft.Description;
            mselEntity.DurationSeconds = draft.DurationMinutes * 60;
            SetMetadata(mselEntity, draft.Metadata);
            // the marking the planner chose for the articles; Write with AI keeps to it
            if (draft.Design?.Marking != null)
                (mselEntity.ArticleMarking, mselEntity.ArticleMarkingText) = ArticleMarkings.Clean(draft.Design.Marking, draft.Design.MarkingText, ArticleMarkings.Exercise);
            if (mselEntity.StartTime == default)
            {
                var now = DateTime.UtcNow;
                mselEntity.StartTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
            }
            mselEntity.ScenarioEvents = new HashSet<ScenarioEventEntity>();
            mselEntity.Moves = new HashSet<MoveEntity>();
            // like a JSON import, team members are not carried over
            foreach (var team in mselEntity.Teams)
            {
                team.TeamUsers = [];
                team.UserTeamRoles = [];
            }

            var templateTeams = new HashSet<string>(mselEntity.Teams.Select(t => t.ShortName), StringComparer.OrdinalIgnoreCase);
            foreach (var team in draft.Teams.Where(t => !templateTeams.Contains(t.ShortName)))
            {
                mselEntity.Teams.Add(new TeamEntity
                {
                    Id = Guid.NewGuid(),
                    Name = string.IsNullOrWhiteSpace(team.Name) ? team.ShortName : team.Name,
                    ShortName = team.ShortName,
                    Description = team.Description
                });
            }
            foreach (var draftTeam in draft.Teams.Where(t => t.Competencies.Any()))
            {
                var team = mselEntity.Teams.First(t => t.ShortName.Equals(draftTeam.ShortName, StringComparison.OrdinalIgnoreCase));
                var existing = new HashSet<Guid>(team.TeamCompetencies.Select(tc => tc.CompetencyId));
                foreach (var id in draftTeam.Competencies.Where(id => existing.Add(competencies[id].Id)))
                    team.TeamCompetencies.Add(new TeamCompetencyEntity { Id = Guid.NewGuid(), TeamId = team.Id, CompetencyId = competencies[id].Id });
            }
            // the duties each team's members pick from in CITE, beside any the template has
            if (mselEntity.UseCite)
            {
                mselEntity.CiteDuties ??= new HashSet<CiteDutyEntity>();
                foreach (var draftTeam in draft.Teams.Where(t => t.Duties?.Any() == true))
                {
                    var team = mselEntity.Teams.First(t => t.ShortName.Equals(draftTeam.ShortName, StringComparison.OrdinalIgnoreCase));
                    var existing = new HashSet<string>(mselEntity.CiteDuties.Where(d => d.TeamId == team.Id).Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (var duty in draftTeam.Duties.Select(d => d?.Trim()).Where(d => !string.IsNullOrEmpty(d)).Take(6).Where(existing.Add))
                        mselEntity.CiteDuties.Add(new CiteDutyEntity { Id = Guid.NewGuid(), MselId = mselEntity.Id, TeamId = team.Id, Name = duty });
                }
            }
            var templateOrgs = new HashSet<string>(mselEntity.Organizations.Select(o => o.ShortName), StringComparer.OrdinalIgnoreCase);
            foreach (var organization in draft.Organizations.Where(o => !templateOrgs.Contains(o.ShortName)))
            {
                mselEntity.Organizations.Add(new OrganizationEntity
                {
                    Id = Guid.NewGuid(),
                    Name = string.IsNullOrWhiteSpace(organization.Name) ? organization.ShortName : organization.Name,
                    ShortName = organization.ShortName,
                    Description = organization.Description,
                    // a written profile is a page; the summary stays one plain line, with the organization's sector, size, industry and country
                    Summary = OneLine(string.Join(" ", new[]
                    {
                        string.IsNullOrWhiteSpace(organization.Summary) ? DataValueDraftService.StripHtml(organization.Description) : organization.Summary.Trim(),
                        MselDraftRules.OrganizationFacts(organization) is { Length: > 0 } facts ? $"({facts})" : null
                    }.Where(x => !string.IsNullOrWhiteSpace(x))))
                });
            }
            // the draft's cards go on the wall after the template's; every team sees every card, and EXCON may post to them
            var cardsByName = new Dictionary<string, CardEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in mselEntity.Cards)
                cardsByName.TryAdd(card.Name.Trim(), card);
            var cardOrder = mselEntity.Cards.Select(c => c.Inject).DefaultIfEmpty(0).Max();
            var draftedCards = new List<CardEntity>();
            foreach (var draftCard in draft.Cards.Where(c => !cardsByName.ContainsKey(c.Name)))
            {
                var card = new CardEntity
                {
                    Id = Guid.NewGuid(),
                    MselId = mselEntity.Id,
                    Name = draftCard.Name,
                    Description = draftCard.Description,
                    Move = 0,
                    Inject = ++cardOrder
                };
                mselEntity.Cards.Add(card);
                draftedCards.Add(card);
                cardsByName[card.Name] = card;
            }
            foreach (var card in mselEntity.Cards)
            {
                foreach (var team in mselEntity.Teams.Where(t => !card.CardTeams.Any(ct => ct.TeamId == t.Id)))
                {
                    card.CardTeams.Add(new CardTeamEntity(card.Id, team.Id)
                    {
                        Id = Guid.NewGuid(),
                        IsShownOnWall = true,
                        CanPostArticles = team.ShortName.Equals("EXCON", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            foreach (var move in draft.Moves)
            {
                mselEntity.Moves.Add(new MoveEntity
                {
                    Id = Guid.NewGuid(),
                    MoveNumber = move.MoveNumber,
                    Description = move.Description,
                    SituationDescription = move.SituationDescription,
                    SituationTime = MselDraftRules.ParseUtc(move.SituationTime),
                    DeltaSeconds = move.StartMinutes * 60
                });
            }

            // Normalize kept only competencies from the list, and the MSEL's list includes every one used
            var mselCompetencies = new HashSet<Guid>(mselEntity.MselCompetencies.Select(mc => mc.CompetencyId));
            foreach (var id in draft.Competencies.Where(id => mselCompetencies.Add(competencies[id].Id)))
                mselEntity.MselCompetencies.Add(new MselCompetencyEntity { Id = Guid.NewGuid(), MselId = mselEntity.Id, CompetencyId = competencies[id].Id });

        }

        /// <summary>The rows, the teams' CITE tasks and the read-in; returns the read-in page, which the Player applications link to</summary>
        public static MselPageEntity AddInjects(MselEntity mselEntity, MselDraft draft, IReadOnlyDictionary<string, CompetencyEntity> competencies,
            Dictionary<MselDraftInject, ScenarioEventEntity> created = null)
        {
            // each move's situation briefing is written with the injects
            foreach (var move in draft.Moves.Where(m => !string.IsNullOrWhiteSpace(m.SituationDescription)))
                if (mselEntity.Moves.FirstOrDefault(m => m.MoveNumber == move.MoveNumber) is MoveEntity entity)
                    entity.SituationDescription = move.SituationDescription;
            var cardsByName = new Dictionary<string, CardEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in mselEntity.Cards)
                cardsByName.TryAdd(card.Name.Trim(), card);
            var draftedCardNames = new HashSet<string>(draft.Cards.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var draftedCards = mselEntity.Cards.Where(c => draftedCardNames.Contains(c.Name.Trim())).ToList();
            var cardField = mselEntity.DataFields.FirstOrDefault(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.CardId));

            // CITE actions go after the template's, numbered within each team's move
            var teamsByShortName = new Dictionary<string, TeamEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var team in mselEntity.Teams.Where(t => t.ShortName != null))
                teamsByShortName.TryAdd(team.ShortName, team);
            foreach (var group in draft.CiteActions.GroupBy(a => (a.MoveNumber, Team: teamsByShortName[a.Team])))
            {
                var actionNumber = mselEntity.CiteActions
                    .Where(a => a.MoveNumber == group.Key.MoveNumber && a.TeamId == group.Key.Team.Id)
                    .Select(a => a.ActionNumber).DefaultIfEmpty(0).Max();
                foreach (var action in group)
                {
                    mselEntity.CiteActions.Add(new CiteActionEntity
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselEntity.Id,
                        TeamId = group.Key.Team.Id,
                        MoveNumber = action.MoveNumber,
                        ActionNumber = ++actionNumber,
                        Description = action.Description
                    });
                }
            }

            // each inject's competencies go in the first Competency column, which needs an option for each
            var competencyField = mselEntity.DataFields
                .Where(f => f.DataType == DataFieldType.Competency)
                .OrderBy(f => f.DisplayOrder)
                .FirstOrDefault();
            if (competencyField != null)
            {
                var options = new HashSet<string>(competencyField.DataOptions.Select(o => o.OptionName), StringComparer.OrdinalIgnoreCase);
                var displayOrder = competencyField.DataOptions.Select(o => o.DisplayOrder).DefaultIfEmpty(0).Max();
                foreach (var id in draft.Injects.SelectMany(i => i.Competencies).Where(options.Add))
                {
                    competencyField.DataOptions.Add(new DataOptionEntity
                    {
                        Id = Guid.NewGuid(),
                        DataFieldId = competencyField.Id,
                        OptionName = id,
                        OptionValue = competencies[id].ShortName,
                        OptionDescription = competencies[id].Description,
                        DisplayOrder = ++displayOrder
                    });
                }
            }

            // with Steamfitter, STARTEX and ENDEX rows tell every participant in Player when play starts and stops,
            // the move-boundary notifications the blueprint-ttx loader adds
            var rows = draft.Injects.ToList();
            var notifications = new Dictionary<MselDraftInject, (string Title, string Text)>();
            var titleField = MselDraftRules.TitleField(mselEntity);
            if (mselEntity.UseSteamfitter)
            {
                // the row the model wrote for a boundary, such as "ENDEX and hot wash", is that boundary rather than a second
                // row beside it; it moves to the boundary's time, first of the rows there, and carries the notification
                void Boundary(int minutes, string title, string text)
                {
                    var row = titleField == null ? null : rows.FirstOrDefault(r =>
                        r.Values.TryGetValue(titleField.Name, out var written) && written != null
                        && System.Text.RegularExpressions.Regex.IsMatch(written, $@"^\W*{title}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    if (row == null)
                    {
                        row = new MselDraftInject { EventType = nameof(EventType.Facilitation) };
                        if (titleField != null)
                            row.Values[titleField.Name] = title;
                    }
                    else
                        rows.Remove(row);
                    row.Minutes = minutes;
                    var at = rows.FindIndex(r => r.Minutes >= minutes);
                    rows.Insert(at < 0 ? rows.Count : at, row);
                    notifications[row] = (title, text);
                }
                // STARTEX opens the first move of play and ENDEX the post-exercise move, so each is where CITE and Gallery change move
                var startex = draft.Moves.Where(m => m.Phase == null).Select(m => (int?)m.StartMinutes).Min() ?? 0;
                var endex = draft.Moves.FirstOrDefault(m => m.Phase == MselDraftRules.PostExercise)?.StartMinutes ?? draft.DurationMinutes;
                Boundary(startex, "STARTEX", "STARTEX: the exercise has started. Review the new material in Gallery and Player.");
                Boundary(endex, "ENDEX", "ENDEX: the exercise has ended. Join the hot wash; thank you for taking part.");
            }

            // one data value per column, as when a planner adds a row
            foreach (var group in rows.GroupBy(i => i.Minutes))
            {
                var groupOrder = 0;
                foreach (var inject in group)
                {
                    var deltaSeconds = inject.Minutes * 60;
                    var scenarioEvent = new ScenarioEventEntity
                    {
                        Id = Guid.NewGuid(),
                        DeltaSeconds = deltaSeconds,
                        GroupOrder = groupOrder++,
                        ScenarioEventType = Enum.Parse<EventType>(inject.EventType, true)
                    };
                    // the push sends only the rows whose integration target names the application
                    if (mselEntity.UseGallery && ArticleFields.IsArticle(mselEntity, inject.Values))
                        scenarioEvent.IntegrationTarget = "Gallery";
                    if (notifications.TryGetValue(inject, out var notification))
                    {
                        scenarioEvent.IntegrationTarget = "Steamfitter";
                        scenarioEvent.SteamfitterTaskId = Guid.NewGuid();
                        scenarioEvent.SteamfitterTask = new SteamfitterTaskEntity
                        {
                            Id = scenarioEvent.SteamfitterTaskId.Value,
                            ScenarioEventId = scenarioEvent.Id,
                            Name = $"{notification.Title} notification",
                            Description = "Tells every participant in Player",
                            TaskType = SteamfitterIntegrationType.Notification,
                            Action = SteamfitterTaskAction.http_post,
                            TriggerCondition = SteamfitterTaskTrigger.Time,
                            ActionParameters = new Dictionary<string, string> { ["notificationText"] = notification.Text },
                            Iterations = 1,
                            UserExecutable = true
                        };
                    }
                    foreach (var dataField in mselEntity.DataFields)
                    {
                        string value;
                        if (dataField == competencyField)
                            value = inject.Competencies.Any() ? string.Join(", ", inject.Competencies) : null;
                        else if (MselDraftRules.IsExpectedActionField(dataField))
                            value = inject.ExpectedAction;
                        // the card's ID, which the copy below moves to the new card's ID
                        else if (dataField == cardField)
                            value = inject.Card != null && cardsByName.TryGetValue(inject.Card, out var injectCard) ? injectCard.Id.ToString() : null;
                        else if (!inject.Values.TryGetValue(dataField.Name, out value))
                        {
                            value = dataField.DataType switch
                            {
                                DataFieldType.Status => MselItemStatus.Pending.ToString(),
                                DataFieldType.DateTime when MselDraftRules.IsServerFilled(dataField) =>
                                    mselEntity.StartTime.AddSeconds(deltaSeconds).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                _ => null
                            };
                        }
                        // every row has a Gallery status color, Gray when the model gave none it could be, so the column is never blank
                        if (dataField.GalleryArticleParameter == nameof(GalleryArticleParameter.Status))
                            value = MselDraftRules.GalleryStatus(value);
                        scenarioEvent.DataValues.Add(new DataValueEntity
                        {
                            Id = Guid.NewGuid(),
                            DataFieldId = dataField.Id,
                            Value = value
                        });
                    }
                    mselEntity.ScenarioEvents.Add(scenarioEvent);
                    created?.TryAdd(inject, scenarioEvent);
                }
            }

            // a drafted card first appears on the wall with its first article, so the wall grows as the story does
            if (cardField != null && draftedCards.Any())
            {
                var at = MselDraftRules.GalleryMovesAndInjects(mselEntity.ScenarioEvents, mselEntity.Moves);
                foreach (var card in draftedCards)
                {
                    var first = mselEntity.ScenarioEvents
                        .Where(e => e.DataValues.Any(v => v.DataFieldId == cardField.Id && v.Value == card.Id.ToString()))
                        .Select(e => at[e])
                        .OrderBy(a => a.Move).ThenBy(a => a.Inject)
                        .Select(a => ((int Move, int Inject)?)a)
                        .FirstOrDefault();
                    if (first is (int move, int inject))
                    {
                        card.Move = move;
                        card.Inject = inject;
                    }
                }
            }

            // the read-in is a page every participant can see, and part of the playbook
            return AddPage(mselEntity, $"{mselEntity.Name} Read-In", draft.ReadIn);
        }

        /// <summary>What needs the whole MSEL: the overview, the facilitator guide, the profile pages and the Player applications</summary>
        /// <summary>The catalog metadata on the Info page; the template's stays where the draft has none</summary>
        public static void SetMetadata(MselEntity mselEntity, MselDraftMetadata metadata)
        {
            if (metadata == null)
                return;
            mselEntity.EducationalLevel = metadata.EducationalLevel ?? mselEntity.EducationalLevel;
            mselEntity.EducationalUse = metadata.EducationalUse ?? mselEntity.EducationalUse;
            mselEntity.CourseMode = metadata.CourseMode ?? mselEntity.CourseMode;
            mselEntity.Subject = metadata.Subject ?? mselEntity.Subject;
            mselEntity.Keywords = metadata.Keywords ?? mselEntity.Keywords;
            mselEntity.Language = metadata.Language ?? mselEntity.Language;
        }

        public static void Finish(MselEntity mselEntity, MselDraft draft, MselPageEntity readInPage)
        {
            // the overview and profiles are pages every participant can see, and part of the playbook;
            // the facilitator guide is for exercise control only
            AddPage(mselEntity, "Exercise Overview", MselDraftPages.Overview(draft, mselEntity, mselEntity.StartTime));
            AddPage(mselEntity, "Facilitator Guide", MselDraftPages.FacilitatorGuide(draft, mselEntity, mselEntity.StartTime), participants: false);
            MselPageEntity organizationProfilesPage = null, teamProfilesPage = null;
            if (draft.Design.ProfilePages)
            {
                organizationProfilesPage = AddPage(mselEntity, "Organization Profiles", MselDraftRules.ProfilesPage("Organization Profiles",
                    "These profiles describe the organizations, agencies, media outlets and other scenario actors participants may need to reference during the exercise. They are background material, not injects.",
                    mselEntity.Organizations.Select(o => ($"{o.Name} ({o.ShortName})", o.Description))));
                teamProfilesPage = AddPage(mselEntity, "Team Profiles", MselDraftRules.ProfilesPage("Team Profiles",
                    "These profiles describe the participant teams and exercise control. They are background material for facilitators and players, not injects.",
                    mselEntity.Teams.Select(t => ($"{t.Name} ({t.ShortName})", t.Description))));
            }

            // in Player, as the blueprint-ttx loader sets it up: every team gets CITE, Gallery, the live MSEL view and the
            // pages; exercise control also gets Steamfitter. The template's own applications reach the drafted teams too.
            if (mselEntity.UsePlayer)
            {
                // the VM app and a map on it show the range's machines, which only a MSEL that builds them with Caster, and plays
                // hands-on, gives its teams; a tabletop's teams have none, so the template's VM applications are left out
                var hasMachines = mselEntity.UseCaster && draft.Design?.Format != "Discussion";
                foreach (var vmApplication in mselEntity.PlayerApplications.Where(a => (a.Url ?? "").Contains("{vmUrl}", StringComparison.OrdinalIgnoreCase)).ToList())
                    if (!hasMachines)
                        mselEntity.PlayerApplications.Remove(vmApplication);
                var participants = mselEntity.Teams.Where(t => !MselDraftCite.IsControl(t)).ToList();
                var control = mselEntity.Teams.Where(MselDraftCite.IsControl).ToList();
                // a team the template already gives applications keeps its own set; the others get theirs by kind
                var withApplications = new HashSet<Guid>(mselEntity.PlayerApplications.SelectMany(a => a.PlayerApplicationTeams).Select(pt => pt.TeamId));
                foreach (var application in mselEntity.PlayerApplications)
                {
                    var assigned = application.PlayerApplicationTeams.Select(pt => mselEntity.Teams.FirstOrDefault(t => t.Id == pt.TeamId)).Where(t => t != null).ToList();
                    var kinds = new List<TeamEntity>();
                    if (assigned.Any(t => !MselDraftCite.IsControl(t)))
                        kinds.AddRange(participants);
                    if (assigned.Any(MselDraftCite.IsControl))
                        kinds.AddRange(control);
                    foreach (var team in kinds.Where(t => !withApplications.Contains(t.Id) && application.PlayerApplicationTeams.All(pt => pt.TeamId != t.Id)))
                        application.PlayerApplicationTeams.Add(new PlayerApplicationTeamEntity { Id = Guid.NewGuid(), PlayerApplicationId = application.Id, TeamId = team.Id });
                }

                void AddApplication(string name, string url, string icon, IEnumerable<TeamEntity> teams, int displayOrder)
                {
                    var forTeams = teams.ToList();
                    if (!forTeams.Any())
                        return;
                    // the template's own application for the same view stays, whatever it is called: CITE without a section
                    // opens on the dashboard, as CITE Dashboard does; the teams it does not reach get it, rather than going without
                    var existing = mselEntity.PlayerApplications.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || PlayerApplicationViews.Same(a.Url, url));
                    if (existing != null)
                    {
                        foreach (var team in forTeams.Where(t => existing.PlayerApplicationTeams.All(pt => pt.TeamId != t.Id)))
                            existing.PlayerApplicationTeams.Add(new PlayerApplicationTeamEntity { Id = Guid.NewGuid(), PlayerApplicationId = existing.Id, TeamId = team.Id, DisplayOrder = displayOrder });
                        return;
                    }
                    var application = new PlayerApplicationEntity
                    {
                        Id = Guid.NewGuid(),
                        MselId = mselEntity.Id,
                        Name = name,
                        Url = url,
                        Icon = icon,
                        Embeddable = true,
                        LoadInBackground = false
                    };
                    foreach (var team in forTeams)
                        application.PlayerApplicationTeams.Add(new PlayerApplicationTeamEntity { Id = Guid.NewGuid(), PlayerApplicationId = application.Id, TeamId = team.Id, DisplayOrder = displayOrder });
                    mselEntity.PlayerApplications.Add(application);
                }
                const string pageIcon = "{blueprintUrl}/assets/img/pencil-ruler-blue.png";
                var everyone = mselEntity.Teams.ToList();
                if (mselEntity.UseCite)
                    AddApplication("CITE Dashboard", "{citeUrl}?section=dashboard&evaluation={citeEvaluationId}{theme}", "{citeUrl}/assets/img/view-dashboard-blue.png", everyone, 1);
                if (mselEntity.UseGallery)
                {
                    AddApplication("Gallery Wall", "{galleryUrl}/?section=wall&exhibit={galleryExhibitId}{theme}", "{galleryUrl}/assets/img/wall-blue.png", everyone, 2);
                    AddApplication("Gallery Archive", "{galleryUrl}/?section=archive&exhibit={galleryExhibitId}{theme}", "{galleryUrl}/assets/img/archive-blue.png", everyone, 7);
                }
                foreach (var (page, order) in new[] { (readInPage, 3), (organizationProfilesPage, 4), (teamProfilesPage, 5) })
                {
                    if (page != null)
                        AddApplication(page == readInPage ? "Read-In" : page.Name, $"{{blueprintUrl}}/mselpage/{page.Id}{{theme}}", pageIcon, everyone, order);
                }
                AddApplication("Exercise Timeline", "{blueprintUrl}/msel/{blueprintMselId}/view{theme}", pageIcon, everyone, 6);
                if (mselEntity.UseSteamfitter)
                {
                    AddApplication("Steamfitter Tasks", "{steamfitterUrl}/scenario/{steamfitterScenarioId}{theme}", "{steamfitterUrl}/assets/img/monkey_wrench.png", control, 9);
                    AddApplication("Steamfitter Scenarios", "{steamfitterUrl}/?scenarioId={steamfitterScenarioId}&viewId={playerViewId}{theme}", "{steamfitterUrl}/assets/img/monkey_wrench.png", control, 10);
                }

                // Player opens on a team's first tab: CITE's dashboard where the MSEL uses CITE, whatever the template calls
                // its CITE application, or else the read-in; the rest keep their order, numbered from 1 with no ties, since
                // the template's applications came with its own numbers and the drafted teams' with none
                const string citeDashboard = "{citeUrl}?section=dashboard&evaluation={citeEvaluationId}{theme}";
                var readInUrl = readInPage == null ? null : $"/mselpage/{readInPage.Id}";
                int First(PlayerApplicationEntity application) =>
                    mselEntity.UseCite && PlayerApplicationViews.Same(application.Url, citeDashboard) ? 0
                    : readInUrl != null && (application.Url ?? "").Contains(readInUrl, StringComparison.OrdinalIgnoreCase) ? 1
                    : 2;
                foreach (var team in mselEntity.Teams)
                {
                    var tabs = mselEntity.PlayerApplications
                        .SelectMany(a => a.PlayerApplicationTeams.Where(pt => pt.TeamId == team.Id).Select(pt => (Application: a, Tab: pt)))
                        .OrderBy(x => First(x.Application)).ThenBy(x => x.Tab.DisplayOrder).ThenBy(x => x.Application.Name)
                        .ToList();
                    for (var i = 0; i < tabs.Count; i++)
                        tabs[i].Tab.DisplayOrder = i + 1;
                }
            }

        }

        public static MselPageEntity AddPage(MselEntity mselEntity, string name, string content, bool participants = true)
        {
            if (content == null)
                return null;
            var pageName = name;
            for (var n = 2; mselEntity.Pages.Any(p => p.Name.Equals(pageName, StringComparison.OrdinalIgnoreCase)); n++)
                pageName = $"{name} {n}";
            var page = new MselPageEntity
            {
                Id = Guid.NewGuid(),
                MselId = mselEntity.Id,
                Name = pageName,
                Content = content,
                AllCanView = participants,
                IncludeInPlaybook = participants
            };
            mselEntity.Pages.Add(page);
            return page;
        }

        private static string OneLine(string text) =>
            string.IsNullOrEmpty(text) || text.Length <= 200 ? text : text[..200] + "...";
    }
}
