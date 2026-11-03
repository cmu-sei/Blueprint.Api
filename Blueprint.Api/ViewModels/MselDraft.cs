// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>What the planner asks for. Nothing is saved when a draft is generated.</summary>
    public class MselDraftRequest
    {
        /// <summary>The template whose columns, teams and integrations the draft uses</summary>
        public Guid TemplateMselId { get; set; }
        /// <summary>The planner's description of the exercise</summary>
        public string Prompt { get; set; }
        /// <summary>
        /// Frameworks whose competencies the draft may assign. When empty, the template's own MSEL
        /// competencies are used; when the template has none, no competencies are assigned.
        /// </summary>
        public List<Guid> CompetencyFrameworkIds { get; set; } = new List<Guid>();
        /// <summary>The planner's answers to the design questions; anything left empty the model decides and notes</summary>
        public MselDraftDesign Design { get; set; } = new MselDraftDesign();
    }

    /// <summary>
    /// The design decisions that change an exercise most, asked before drafting as the author-ttx workflow does.
    /// Every one is optional.
    /// </summary>
    public class MselDraftDesign
    {
        /// <summary>Discussion (a tabletop), Functional (hands-on in the range) or Hybrid</summary>
        public string Format { get; set; }
        /// <summary>How participants are organized: one combined staff, functional cells, parallel teams...</summary>
        public string Structure { get; set; }
        /// <summary>The CITE scoring model: Template, QuestionsByMove, MissionImpact, Likelihood, Nciss or None</summary>
        public string CiteAssessment { get; set; }
        public int? Moves { get; set; }
        /// <summary>How long the exercise runs in real time, in minutes</summary>
        public int? DurationMinutes { get; set; }
        /// <summary>How much scenario time the exercise covers, in the planner's words, e.g. "three days"</summary>
        public string ScenarioSpan { get; set; }
        /// <summary>Whether the last move is a brief to leadership</summary>
        public bool? LeadershipBrief { get; set; }
        /// <summary>
        /// What each Gallery card holds: Sources (a source or thread), Organizations, Sectors (critical infrastructure),
        /// Countries or Terrain (networks, subnets and key cyber terrain); empty for the draft to choose
        /// </summary>
        public string CardGrouping { get; set; }
        /// <summary>Whether to add Team Profiles and Organization Profiles pages</summary>
        public bool ProfilePages { get; set; } = true;
        /// <summary>The CISA critical infrastructure sectors the exercise covers; empty for the draft to choose</summary>
        public List<string> Sectors { get; set; } = new List<string>();
        /// <summary>How long before STARTEX the doors open: the pre-exercise move, in minutes; 15 when empty</summary>
        public int? LeadMinutes { get; set; }
        /// <summary>The banner on every Gallery article: None, Exercise, Tlp or Classification; empty for the template's</summary>
        public string Marking { get; set; }
        /// <summary>The TLP level, or the classification banner's text</summary>
        public string MarkingText { get; set; }
    }

    /// <summary>A drafted MSEL in a flat form the model can fill and the planner can edit before creating it</summary>
    public class MselDraft
    {
        /// <summary>Identifies the draft's AI calls until it becomes a MSEL, which then takes them over</summary>
        public Guid DraftId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int DurationMinutes { get; set; }
        /// <summary>The design the draft was written to, so import builds the same pages and CITE model</summary>
        public MselDraftDesign Design { get; set; } = new MselDraftDesign();
        public MselDraftScenario Scenario { get; set; } = new MselDraftScenario();
        /// <summary>The catalog metadata on the MSEL's Info page</summary>
        public MselDraftMetadata Metadata { get; set; } = new MselDraftMetadata();
        /// <summary>What participants are trained to do; each inject and the AAR trace back to one</summary>
        public List<MselDraftObjective> Objectives { get; set; } = new List<MselDraftObjective>();
        public List<MselDraftMove> Moves { get; set; } = new List<MselDraftMove>();
        public List<MselDraftTeam> Teams { get; set; } = new List<MselDraftTeam>();
        public List<MselDraftOrganization> Organizations { get; set; } = new List<MselDraftOrganization>();
        /// <summary>Gallery cards, in the order the wall shows them; each Gallery article names one</summary>
        public List<MselDraftCard> Cards { get; set; } = new List<MselDraftCard>();
        public List<MselDraftInject> Injects { get; set; } = new List<MselDraftInject>();
        /// <summary>The read-in participants get before the first move, in HTML; it becomes a page of the MSEL</summary>
        public string ReadIn { get; set; }
        /// <summary>What each team should do in each move, which CITE lists for the team to check off</summary>
        public List<MselDraftCiteAction> CiteActions { get; set; } = new List<MselDraftCiteAction>();
        /// <summary>Competency ID numbers the exercise exercises, from the competencies it may choose from</summary>
        public List<string> Competencies { get; set; } = new List<string>();
        /// <summary>Assumptions and open questions the model wants the planner to check</summary>
        public List<string> PlannerNotes { get; set; } = new List<string>();
        /// <summary>
        /// The scenario's indicators, written once with the injects, so every alert, report, email and CTI event names the
        /// same hosts, addresses, domains and hashes; null for a draft written in one step
        /// </summary>
        public MselIndicatorSet Indicators { get; set; }
    }

    /// <summary>
    /// The one set of indicators a scenario's documents share: the defenders' own hosts, the organizations' domains, and the
    /// adversary's infrastructure. Every value is fictional and safe: documentation and private address ranges, reserved
    /// domains, and hashes Blueprint makes up.
    /// </summary>
    public class MselIndicatorSet
    {
        /// <summary>The adversary's fictional name, as CTI reports call it</summary>
        public string ThreatActor { get; set; }
        /// <summary>The defenders' hosts: a Caster workspace's machines where the MSEL has one, else made up</summary>
        public List<MselIndicatorAsset> Assets { get; set; } = new List<MselIndicatorAsset>();
        /// <summary>Each organization's own domain, for its email addresses and web sites</summary>
        public List<MselIndicatorDomain> Domains { get; set; } = new List<MselIndicatorDomain>();
        /// <summary>The adversary's infrastructure: C2 addresses, domains, file hashes, sender addresses and user agents</summary>
        public List<MselIndicator> Adversary { get; set; } = new List<MselIndicator>();
        /// <summary>Caster when the hosts are a Caster workspace's machines; empty when they were made up</summary>
        public string AssetSource { get; set; }
    }

    public class MselIndicatorAsset
    {
        public string HostName { get; set; }
        /// <summary>Workstation, Server, DomainController, Firewall, Router, WebServer, Database, MailServer or Other</summary>
        public string Role { get; set; }
        public string IpAddress { get; set; }
        /// <summary>The network the host is on, in CIDR form</summary>
        public string Network { get; set; }
        /// <summary>The host's gateway, where its Caster workspace gives one</summary>
        public string Gateway { get; set; }
    }

    public class MselIndicatorDomain
    {
        /// <summary>The organization's short name</summary>
        public string Organization { get; set; }
        public string Domain { get; set; }
    }

    public class MselIndicator
    {
        /// <summary>ip, domain, sha256, email or user-agent</summary>
        public string Type { get; set; }
        public string Value { get; set; }
        /// <summary>What it is for, in a few words: C2 server, phishing sender, loader DLL</summary>
        public string Purpose { get; set; }
        /// <summary>For a file: its name, which the documents may name it by</summary>
        public string Name { get; set; }
        /// <summary>For a file: the same file's MD5, made up with its SHA-256</summary>
        public string Md5 { get; set; }
        /// <summary>For a file: the same file's SHA-1</summary>
        public string Sha1 { get; set; }
    }

    public class MselDraftMove
    {
        public int MoveNumber { get; set; }
        /// <summary>PreExercise or PostExercise for the moves before STARTEX and after ENDEX; empty for a move of play</summary>
        public string Phase { get; set; }
        public string Description { get; set; }
        public string SituationDescription { get; set; }
        /// <summary>Minutes from the start of the exercise</summary>
        public int StartMinutes { get; set; }
        /// <summary>
        /// The scenario's date and time when the move opens, in ISO 8601 UTC. It runs on its own clock and can jump
        /// ahead of the exercise clock, e.g. the next move is the next morning.
        /// </summary>
        public string SituationTime { get; set; }
        /// <summary>What the move is for: the reasoning it makes participants do</summary>
        public string Purpose { get; set; }
        /// <summary>The decisions participants are expected to reach in the move</summary>
        public List<string> ExpectedDecisions { get; set; } = new List<string>();
        /// <summary>Notes for controllers and facilitators, never shown to participants</summary>
        public string ControllerNotes { get; set; }
        /// <summary>How many injects the design plans for the move, which the injects step writes to</summary>
        public int? PlannedInjects { get; set; }
        /// <summary>Discussion questions for the move, when the CITE assessment is questions by move</summary>
        public List<string> CiteQuestions { get; set; } = new List<string>();
    }

    /// <summary>The IEEE 2881 learning metadata Blueprint keeps on the MSEL</summary>
    public class MselDraftMetadata
    {
        /// <summary>Beginner, Intermediate or Advanced</summary>
        public string EducationalLevel { get; set; }
        /// <summary>Assessment, Instruction or Professional Support</summary>
        public string EducationalUse { get; set; }
        /// <summary>Online, Onsite or Blended</summary>
        public string CourseMode { get; set; }
        /// <summary>The topic areas the exercise covers</summary>
        public string Subject { get; set; }
        /// <summary>Comma-separated search tags</summary>
        public string Keywords { get; set; }
        /// <summary>The language of the exercise material, as a BCP 47 tag such as en-US</summary>
        public string Language { get; set; }
    }

    public class MselDraftScenario
    {
        /// <summary>The one-line problem</summary>
        public string Problem { get; set; }
        public string Premise { get; set; }
        /// <summary>The operational environment: political, military, economic, social, information, infrastructure, physical, time</summary>
        public string Environment { get; set; }
        /// <summary>How far the scenario may escalate</summary>
        public string EscalationCeiling { get; set; }
        public List<string> Assumptions { get; set; } = new List<string>();
        /// <summary>The CISA critical infrastructure sectors the scenario touches, the primary one first</summary>
        public List<string> Sectors { get; set; } = new List<string>();
        public string PrimarySector { get; set; }
    }

    /// <summary>A training objective as task, condition and standard</summary>
    public class MselDraftObjective
    {
        /// <summary>O1, O2 ... referred to by injects</summary>
        public string Id { get; set; }
        public string Task { get; set; }
        public string Condition { get; set; }
        public string Standard { get; set; }
        /// <summary>Competency ID numbers the objective exercises</summary>
        public List<string> Competencies { get; set; } = new List<string>();
    }

    public class MselDraftTeam
    {
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
        /// <summary>Competency ID numbers the team is expected to demonstrate</summary>
        public List<string> Competencies { get; set; } = new List<string>();
        /// <summary>The duties members of the team take on, which CITE lets each member pick: Incident Lead, Scribe ...</summary>
        public List<string> Duties { get; set; } = new List<string>();
    }

    public class MselDraftOrganization
    {
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
        /// <summary>One line on who they are; when empty, the description's text is used</summary>
        public string Summary { get; set; }
        /// <summary>The CISA critical infrastructure sector the organization belongs to; empty for one outside them, such as a news outlet</summary>
        public string Sector { get; set; }
        /// <summary>Small, Medium, Large or Enterprise</summary>
        public string Size { get; set; }
        /// <summary>What it does within its sector, e.g. regional acute-care hospital</summary>
        public string Industry { get; set; }
        public string Country { get; set; }
    }

    public class MselDraftCiteAction
    {
        public int MoveNumber { get; set; }
        /// <summary>The short name of the team that should take the action</summary>
        public string Team { get; set; }
        public string Description { get; set; }
    }
    public class MselDraftCard
    {
        public string Name { get; set; }
        /// <summary>What the card's articles are about, shown on the wall</summary>
        public string Description { get; set; }
    }

    public class MselDraftInject
    {
        /// <summary>Minutes from the start of the exercise; the move follows from the time</summary>
        public int Minutes { get; set; }
        /// <summary>Inject, Information or Facilitation</summary>
        public string EventType { get; set; } = "Inject";
        /// <summary>Values keyed by the template's column (data field) name</summary>
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
        /// <summary>Competency ID numbers participants demonstrate by acting on the inject</summary>
        public List<string> Competencies { get; set; } = new List<string>();
        /// <summary>The name of the Gallery card the article goes on; empty for an inject that is not a Gallery article</summary>
        public string Card { get; set; }
        /// <summary>What participants are expected to do when they get the inject</summary>
        public string ExpectedAction { get; set; }
        /// <summary>The ID of the objective the inject serves</summary>
        public string Objective { get; set; }
        /// <summary>The values of the indicator set the inject's document names: host names, addresses, domains, hashes</summary>
        public List<string> Indicators { get; set; } = new List<string>();
        /// <summary>The ATT&amp;CK techniques the inject shows, as ID and name, e.g. T1566.001 Spearphishing Attachment</summary>
        public List<string> Techniques { get; set; } = new List<string>();
        /// <summary>CtiEvent for an Intel article written as a MISP-style CTI event; empty for the source type's usual layout</summary>
        public string Layout { get; set; }
    }

    /// <summary>
    /// A question the design step asks the planner, whose answer would change the exercise. Each has the answer the
    /// draft uses if the planner gives none.
    /// </summary>
    public class MselDesignQuestion
    {
        public string Id { get; set; }
        public string Question { get; set; }
        /// <summary>Why the answer matters to the exercise</summary>
        public string Why { get; set; }
        public string Suggested { get; set; }
    }

    public class MselDesignAnswer
    {
        public string Question { get; set; }
        public string Answer { get; set; }
    }

    /// <summary>A reviewed design to create its MSEL from, which the rest of the draft is then written into</summary>
    public class MselDesignBuild
    {
        /// <summary>The ID the new MSEL takes, chosen by the browser so it can open the MSEL's page before it is created; a new one when empty</summary>
        public Guid? MselId { get; set; }
        public Guid TemplateMselId { get; set; }
        /// <summary>The planner's description of the exercise, which the injects are written from too</summary>
        public string Prompt { get; set; }
        /// <summary>The design as reviewed: no injects yet</summary>
        public MselDraft Draft { get; set; }
        public List<Guid> CompetencyFrameworkIds { get; set; } = new List<Guid>();
        public List<MselDesignAnswer> Answers { get; set; } = new List<MselDesignAnswer>();
    }

    /// <summary>Where building a MSEL from its AI draft stands, and what is left to write</summary>
    public class MselBuildStatus
    {
        public Guid MselId { get; set; }
        /// <summary>Injects, Articles or Finish; empty once the MSEL is built</summary>
        public string Stage { get; set; }
        /// <summary>How many of the rows, articles and profiles to write at once</summary>
        public int Parallel { get; set; }
        /// <summary>How many injects the design planned, while they are outlined; null once they are</summary>
        public int? PlannedInjects { get; set; }
        /// <summary>The other injects whose long text is still to write</summary>
        public List<MselBuildItem> Injects { get; set; } = new List<MselBuildItem>();
        /// <summary>The Gallery articles still to write</summary>
        public List<MselBuildItem> Articles { get; set; } = new List<MselBuildItem>();
        /// <summary>The team and organization profiles still to write</summary>
        public List<MselBuildItem> Profiles { get; set; } = new List<MselBuildItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class MselBuildItem
    {
        /// <summary>The scenario event, team or organization</summary>
        public Guid Id { get; set; }
        /// <summary>article, inject, team or organization</summary>
        public string Kind { get; set; }
        public string Name { get; set; }
    }

    /// <summary>A written article or profile to save, after its graphics are drawn as PNG</summary>
    public class MselBuildContent
    {
        public string Html { get; set; }
        /// <summary>What the server corrected in what was written, such as values outside the scenario's indicator set</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>A competency the draft assigned, named so the planner can review it</summary>
    public class MselDraftCompetency
    {
        public string IdNumber { get; set; }
        public string ShortName { get; set; }
    }

    /// <summary>A template column the draft fills</summary>
    public class MselDraftField
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public List<string> Options { get; set; } = new List<string>();
    }

    public class MselDraftResult
    {
        public Guid TemplateMselId { get; set; }
        public string TemplateName { get; set; }
        public MselDraft Draft { get; set; }
        /// <summary>The template columns the model filled, in display order</summary>
        public List<MselDraftField> Fields { get; set; } = new List<MselDraftField>();
        /// <summary>Things the server corrected or dropped from the model's answer</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        /// <summary>The competencies the draft assigned, with their names</summary>
        public List<MselDraftCompetency> Competencies { get; set; } = new List<MselDraftCompetency>();
        /// <summary>From the design step: what the planner should decide before the injects are written</summary>
        public List<MselDesignQuestion> Questions { get; set; } = new List<MselDesignQuestion>();
    }

    /// <summary>A reviewed draft to create as a new MSEL</summary>
    public class MselDraftImport
    {
        public Guid TemplateMselId { get; set; }
        public MselDraft Draft { get; set; }
        /// <summary>The frameworks the draft chose from, so its competencies are checked against the same list</summary>
        public List<Guid> CompetencyFrameworkIds { get; set; } = new List<Guid>();
    }

    /// <summary>One of a reviewed draft's Gallery articles, to be written out as a document of its source type</summary>
    public class ArticleDraftRequest
    {
        public Guid TemplateMselId { get; set; }
        public MselDraft Draft { get; set; }
        /// <summary>The position of the article among the draft's injects</summary>
        public int InjectIndex { get; set; }
    }

    public class ArticleDraftResult
    {
        /// <summary>The column the HTML goes in</summary>
        public string FieldName { get; set; }
        /// <summary>The article, in the HTML Gallery shows as written; each graphic is an SVG data URI image</summary>
        public string Html { get; set; }
        /// <summary>Corrections made to the model's HTML, and its assumptions</summary>
        public List<string> Notes { get; set; } = new List<string>();
        /// <summary>What the server corrected that the planner should know of, such as values outside the scenario's indicator set</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// A team's or organization's profile to write: on a MSEL (MselId), or on a draft that is not one yet (Draft).
    /// Nothing is saved.
    /// </summary>
    public class ProfileDraftRequest
    {
        public Guid? MselId { get; set; }
        public MselDraft Draft { get; set; }
        /// <summary>team or organization</summary>
        public string Kind { get; set; }
        public string ShortName { get; set; }
        public string Name { get; set; }
        public string Summary { get; set; }
        /// <summary>The description as it is now, which the profile keeps unless told otherwise</summary>
        public string Description { get; set; }
        /// <summary>What the planner wants changed or added</summary>
        public string Instruction { get; set; }
    }

    public class ProfileDraftResult
    {
        /// <summary>The profile, in the HTML Blueprint keeps; the logo is an SVG data URI image</summary>
        public string Html { get; set; }
        public List<string> Notes { get; set; } = new List<string>();
    }
}
