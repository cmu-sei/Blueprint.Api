// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Net;

namespace Blueprint.Api.Infrastructure.Exceptions
{
    /// <summary>
    /// Thrown when a request would duplicate something that already exists,
    /// e.g. importing a competency framework whose ID number is already in use.
    /// </summary>
    public class ConflictException : Exception, IApiException
    {
        public ConflictException(string message)
            : base(message)
        {
        }

        public HttpStatusCode GetStatusCode()
        {
            return HttpStatusCode.Conflict;
        }
    }
}
