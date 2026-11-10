// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Net;

namespace Blueprint.Api.Infrastructure.Exceptions
{
    public interface IApiException
    {
        HttpStatusCode GetStatusCode();

        /// <summary>The problem type the error answers with, for a client that handles this problem its own way.</summary>
        string ProblemType => null;
    }
}

