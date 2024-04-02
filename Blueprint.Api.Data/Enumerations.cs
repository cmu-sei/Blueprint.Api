// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

namespace Blueprint.Api.Data.Enumerations
{
    public enum ItemStatus
    {
        Pending = 0,
        Entered = 10,
        Approved = 20,
        Complete = 30,
        Deployed = 40,
        Archived = 50
    }

    public enum IntegrationType {
        Deploy = 10,
        // Clone = 20,  add this when cloning is implemented
        Connect = 30
    }

    public enum EventExecutionStatus
    {
        Executed = 10,
        Completed = 20,
        Succeeded = 30,
        Failed = 40,
        Expired = 50
    }

    public enum DataFieldType{
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
        DeliveryMethod = 160     // values will currently be filled from Notification, Email, and Gallery
    }

    public enum MselRole{
        Owner = 10,
        Editor = 20,
        Approver = 30,
        MoveEditor = 40,
        Viewer = 50,
        Evaluator = 60
    }

    public enum TeamRole{
        Observer = 80,
        Inviter = 90,
        Incrementer = 100,
        Modifier = 110,
        Submitter = 120
    }

    public enum GalleryArticleParameter{
      Name,
      Description,
      Status,
      SourceType,
      SourceName,
      Url,
      DatePosted,
      OpenInNewTab,
      CardId,
      DeliveryMethod,
      ToOrg,
      FromOrg,
      Summary
    }

    public enum GallerySourceType{
        News,
        Social,
        Email,
        Phone,
        Intel,
        Reporting
    }

}

