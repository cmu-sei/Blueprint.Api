// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// App extra: questions blueprint's tests ask of the shared HubRecorder<MainHub>, keyed on the groups a test
// names plus MainHub's four fixed groups.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Hubs;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// Reads a <see cref="HubRecorder{THub}"/> of <see cref="MainHub"/> by audience. Every question names the
/// groups it is about (a MSEL's id, a unit's, a team's, a user's), and <see cref="AlwaysRead"/> is read as
/// well, so "who was told" covers every audience a handler addresses that the test could not have named.
/// </summary>
/// <remarks>
/// MainHub's event handlers address clients only through <c>Clients.Group</c>, so the groups below and the
/// ids of the rows a test seeded are every audience there is.
/// </remarks>
internal static class MainHubBroadcasts
{
    /// <summary>
    /// The four groups <c>MainHub</c> declares, then the two a handler names for a row with no MSEL
    /// (<c>""</c>) or whose parent is gone (the all-zeros guid), in this order after the named groups.
    /// </summary>
    public static readonly string[] AlwaysRead =
    [
        MainHub.ADMIN_DATA_GROUP, MainHub.USER_GROUP, MainHub.ROLE_GROUP, MainHub.GROUP_GROUP,
        string.Empty, System.Guid.Empty.ToString()
    ];

    /// <summary>
    /// The audiences among <paramref name="groups"/> and <see cref="AlwaysRead"/> that were sent
    /// <paramref name="method"/>: the named groups first, in the order given, then the others.
    /// </summary>
    public static IReadOnlyList<string> Recipients(
        this HubRecorder<MainHub> hub, string method, params object[] groups) =>
        [.. Audiences(groups).Where(group => hub.ToGroup(group).Any(x => x.Method == method))];

    /// <summary>Every <paramref name="method"/> broadcast to those audiences, audience by audience.</summary>
    public static IReadOnlyList<HubBroadcast> Of(
        this HubRecorder<MainHub> hub, string method, params object[] groups) =>
        [.. Sent(hub, groups).Where(x => x.Method == method)];

    /// <summary>Every broadcast to those audiences, audience by audience.</summary>
    public static IReadOnlyList<HubBroadcast> Sent(this HubRecorder<MainHub> hub, params object[] groups) =>
        [.. Audiences(groups).SelectMany(hub.ToGroup)];

    extension(HubBroadcast broadcast)
    {
        /// <summary>The first argument, which every handler sends as the entity or its id; null when none.</summary>
        public object Payload => broadcast.Arguments.Length > 0 ? broadcast.Arguments[0] : null;
    }

    private static IEnumerable<string> Audiences(object[] groups) =>
        groups.Select(x => x?.ToString() ?? string.Empty).Concat(AlwaysRead).Distinct();
}
