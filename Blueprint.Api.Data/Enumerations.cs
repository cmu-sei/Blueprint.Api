// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

namespace Blueprint.Api.Data.Enumerations
{
    public enum ItemStatus
    {
        Pending = 0,
        Entered = 10,
        Approved = 20,
        Complete = 30
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
        Move = 150,
        DeliveryMethod = 160
    }

    public enum MselRole{
        Owner = 10,
        Editor = 20,
        Approver = 30,
        MoveEditor = 40,
        Viewer = 50,
        Facilitator = 60,
        GalleryObserver = 70,
        CiteObserver = 80
    }

    public enum GalleryArticleParameter{
      Name,
      Description,
      Move,
      Inject,
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

