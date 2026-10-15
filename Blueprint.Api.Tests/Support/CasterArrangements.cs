// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Caster.Api.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Arrangements on <see cref="BlueprintAppFactory.Caster"/>, the substituted client <c>CasterService</c>
/// calls with the caller's own token: a directory Caster shows the caller, and one it refuses.
/// </summary>
/// <remarks>
/// Kept in one place because the MSEL create, update, push and import paths all ask the same question
/// (<c>CasterService.GetDirectoryAsync</c>), and the answer is two calls on the client: the directory, then
/// its project.
/// </remarks>
public static class CasterArrangements
{
    /// <summary>The exception the generated client throws for a status it was not told to expect.</summary>
    public static ApiException Answering(HttpStatusCode status) =>
        new($"Caster answered {(int)status}.", (int)status, "", new Dictionary<string, IEnumerable<string>>(), null);

    /// <summary>Caster shows the caller the directory <paramref name="directoryId"/>, in a project of its own.</summary>
    public static void ShowsDirectory(this ICasterApiClient caster, Guid directoryId, string name = "Range")
    {
        var projectId = Guid.NewGuid();

        caster.GetDirectoryAsync(directoryId, false, false, Arg.Any<CancellationToken>())
            .Returns(new Directory { Id = directoryId, Name = name, ProjectId = projectId });
        caster.GetProjectAsync(projectId, Arg.Any<CancellationToken>())
            .Returns(new Project { Id = projectId, Name = "Project" });
    }

    /// <summary>Caster answers <paramref name="status"/> when asked for the directory <paramref name="directoryId"/>.</summary>
    public static void RefusesDirectory(
        this ICasterApiClient caster, Guid directoryId, HttpStatusCode status = HttpStatusCode.Forbidden) =>
        caster.GetDirectoryAsync(directoryId, false, false, Arg.Any<CancellationToken>()).Throws(Answering(status));
}
