// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;

namespace Blueprint.Api.Infrastructure.Exceptions
{
    /// <summary>Thrown when an uploaded file is not an ATT&amp;CK STIX 2.x bundle Blueprint can read.</summary>
    public class InvalidAttackBundleException : Exception, IApiException
    {
        public InvalidAttackBundleException(string message)
            : base(message)
        {
        }

        public HttpStatusCode GetStatusCode()
        {
            return HttpStatusCode.BadRequest;
        }
    }
}
