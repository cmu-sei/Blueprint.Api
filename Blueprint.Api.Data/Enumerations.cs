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
        Organization = 50,
        Html = 60,
        Card = 70,
        SourceType = 80,
        Team = 90,
        TeamsMultiple = 100,
        Status = 110
    }

    public enum MselRole{
        Owner = 10,
        Editor = 20,
        Approver = 30,
        MoveEditor = 40
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
      FromOrg
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

