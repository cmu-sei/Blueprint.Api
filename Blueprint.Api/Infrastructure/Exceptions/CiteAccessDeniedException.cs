// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Blueprint.Api.Infrastructure.Exceptions
{
    /// <summary>
    /// A 403 that CITE, not Blueprint, decided: the caller may not read in full the CITE item they asked Blueprint to
    /// use. Its problem type lets the UI say so, apart from a refusal of Blueprint's own.
    /// </summary>
    // re-implementing IApiException is what gives ProblemType to the interface, which ForbiddenException leaves at its default
    public class CiteAccessDeniedException : ForbiddenException, IApiException
    {
        public const string Type = "urn:crucible:blueprint:cite-access-denied";

        public CiteAccessDeniedException(string message)
            : base(message)
        {
        }

        public string ProblemType => Type;
    }
}
