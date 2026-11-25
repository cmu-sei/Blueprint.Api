// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

namespace Blueprint.Api.Data.Enumerations
{
    public enum MselItemStatus
    {
        Pending = 0,
        Entered = 10,
        Approved = 20,
        Complete = 30,
        Pushing = 35,
        Pulling = 36,
        Deployed = 40,
        Archived = 50
    }

    public enum IntegrationType
    {
        Deploy = 10,
        // Clone = 20,  add this when cloning is implemented
        Connect = 30
    }

    public enum EventType
    {
        Inject = 10,
        Information = 20,
        Facilitation = 30
    }

    public enum DataFieldType
    {
        String = 0,
        Integer = 10,
        Double = 20,
        Boolean = 30,
        DateTime = 40,
        Organization = 50,    // includes teams and organizations, currently used for the from
        Html = 60,
        Card = 70,
        SourceType = 80,
        Team = 90,    // a single team, currently used for the team assigned to an inject
        TeamsMultiple = 100,    // currently used for the to of an inject
        Status = 110,    // used for the status of the inject as it is being created and approved
        User = 120,    // user assigned to the inject.  For execution, etc.
        Checkbox = 130,    // used to track statuses about the inject.
        Url = 140,    // link to other information
        Move = 150,     // values will be filled from the MSEL moves
        // 160 is not reused: it was DeliveryMethod, then IntegrationTarget, and older databases may still hold it
        Competency = 170,    // competencies (e.g., NICE Framework tasks) for tracking and xAPI logging
        IpAddress = 180,    // a single IPv4 or IPv6 address
        Network = 190,    // an IPv4 or IPv6 network in CIDR form (10.1.0.0/24)
        AttackTechnique = 200,    // A technique from the MSEL's attack technique pool
        DefensiveTechnique = 201    // A technique from the MSEL's defensive technique pool
    }

    public enum MselRole
    {
        Owner = 10,
        Editor = 20,
        Approver = 30,
        MoveEditor = 40,
        Viewer = 50,
        Evaluator = 60,
        // runs the MSEL while it is deployed: starts moves and groups and runs its automation tasks
        Facilitator = 70
    }

    // what an automation task does; the action catalog (automationActions.json) describes each one's parameters
    public enum AutomationAction
    {
        // http requests, numbered as Steamfitter numbered them
        http_get = 11,
        http_post = 12,
        http_put = 13,
        http_delete = 14,
        // Blueprint's own actions, aimed at the MSEL's own Player view, CITE evaluation and Gallery exhibit
        player_notification = 100,
        cite_situation_update = 110,
        cite_move_set = 120,
        gallery_move_inject_set = 130,
        // actions on the VMs of a Player view, through the Player VM API
        vm_hw_power_on = 200,
        vm_hw_power_off = 201,
        vm_snapshot_create = 210,
        vm_snapshot_revert = 211,
        vm_snapshot_delete = 212,
        guest_process_run = 220,
        guest_process_run_fast = 221,
        guest_file_read = 230,
        guest_file_upload_content = 231,
        guest_file_upload_file = 232,
        // commands over SSH
        core_remote = 300,
        linux_file_touch = 301,
        linux_rm = 302,
        send_email = 400
    }

    // what starts an automation task
    public enum AutomationTrigger
    {
        Manual = 0,          // a facilitator runs it
        AtEventTime = 10,    // the MSEL's start time plus its scenario event's DeltaSeconds
        OnSuccess = 20,      // its trigger task succeeded
        OnFailure = 30,      // its trigger task failed
        OnCompletion = 40,   // its trigger task succeeded or failed
        OnExpiration = 50    // its trigger task ran out of time
    }

    // when a task that runs more than once stops
    public enum AutomationIterationTermination
    {
        IterationCount = 0,
        UntilSuccess = 10,
        UntilFailure = 20
    }

    public enum AutomationStatus
    {
        Pending = 10,     // waiting for its due time
        Queued = 20,      // claimed by a worker
        Running = 30,
        Succeeded = 40,
        Failed = 50,
        Expired = 60,
        Cancelled = 70,
        Error = 80,       // it could not run: no VM matched, the API restarted while it ran, ...
        Skipped = 90      // its scheduled time passed while it could not run
    }

    // a run of the deployed exercise, or a test run while the MSEL is being written
    public enum AutomationRunKind
    {
        Exercise = 0,
        Test = 10
    }

    // what created a run
    public enum AutomationRunSource
    {
        Manual = 0,
        Schedule = 10,
        Chain = 20,
        Rerun = 30,
        GroupStart = 40,
        // a participant ran it from the MSEL's Tasks page
        Participant = 50
    }

    public enum GalleryArticleParameter
    {
        Name,
        Description,
        Status,
        SourceType,
        SourceName,
        Url,
        DatePosted,
        OpenInNewTab,
        CardId,
        ToOrg,
        FromOrg,
        Summary
    }

    // Gallery article statuses, named for the color the wall shows, best to worst
    public enum GalleryItemStatus
    {
        Gray,
        Green,
        Yellow,
        Orange,
        Red
    }

    public enum GallerySourceType
    {
        News,
        Social,
        Email,
        Phone,
        Intel,
        Reporting,
        Orders
    }

    // where a scenario event is delivered: as a Gallery article, or not
    public enum IntegrationTarget
    {
        Gallery
    }

    public enum SystemPermission
    {
        CreateMsels = 1,
        ViewMsels = 2,
        EditMsels = 3,
        ManageMsels = 4,
        ViewUnits = 5,
        ManageUnits = 6,
        ViewOrganizations = 7,
        ManageOrganizations = 8,
        ViewDataFields = 9,
        ManageDataFields = 10,
        ViewInjectTypes = 11,
        ManageInjectTypes = 12,
        ViewCatalogs = 13,
        ManageCatalogs = 14,
        ViewGalleryCards = 15,
        ManageGalleryCards = 16,
        ViewCiteActions = 17,
        ManageCiteActions = 18,
        ViewCiteDuties = 19,
        ManageCiteDuties = 20,
        ViewUsers = 21,
        ManageUsers = 22,
        ViewRoles = 23,
        ManageRoles = 24,
        ViewGroups = 25,
        ManageGroups = 26,
        ViewCompetencyFrameworks = 27,
        ManageCompetencyFrameworks = 28,
        ViewPlayerApplications = 29,
        ManagePlayerApplications = 30
    }

    // CITE's own values, as its database stores them. The CITE client numbers its copies of these
    // 0, 1, 2..., so convert between the two by name.
    public enum CiteScoringOptionSelection
    {
        Single = 0,
        Multiple = 10,
        None = 20
    }

    public enum CiteRightSideDisplay
    {
        ScoreSummary = 0,
        HtmlBlock = 10,
        EmbeddedUrl = 20,
        Scoresheet = 30,
        None = 40
    }

    /// <summary>The file format a technique framework's releases are published in.</summary>
    public enum TechniqueFileFormat
    {
        StixBundle = 0,
        JsonLdOntology = 1
    }

}
