// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;

namespace Blueprint.Api.Infrastructure.Exceptions
{
    /// <summary>
    /// Thrown when the request itself is unacceptable, e.g. an uploaded file that is not an image.
    /// </summary>
    public class BadRequestException : Exception, IApiException
    {
        public BadRequestException(string message)
            : base(message)
        {
        }

        public HttpStatusCode GetStatusCode()
        {
            return HttpStatusCode.BadRequest;
        }
    }
}
