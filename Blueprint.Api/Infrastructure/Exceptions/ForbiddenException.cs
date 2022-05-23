// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Net;
using System.Text.RegularExpressions;

namespace Blueprint.Api.Infrastructure.Exceptions
{
    public class ForbiddenException : Exception, IApiException
    {
        public ForbiddenException()
            : base("Insufficient Permissions")
        {
        }

        public ForbiddenException(string message)
            : base(message)
        {
        }

        public HttpStatusCode GetStatusCode()
        {
            return HttpStatusCode.Forbidden;
        }
    }
}

