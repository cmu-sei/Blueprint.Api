// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.


using System;
using System.Security.Claims;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class NotificationExtensions
    {
        public static string ViewBroadcastGroup(Guid id)
        {
            return "View_" + id.ToString();
        }

        public static string TeamBroadcastGroup(Guid id)
        {
            return "Team_" + id.ToString();
        }

        public static string UserBroadcastGroup(Guid id)
        {
            return "User_" + id.ToString();
        }

    }
}

