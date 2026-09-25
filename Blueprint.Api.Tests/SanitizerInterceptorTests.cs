// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Attributes;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// <c>SanitizerInterceptor</c>: which properties are stripped of hostile HTML on the way to the
/// database, when the interceptor runs, and what it does to the object the caller still holds.
/// </summary>
/// <remarks>
/// An ordinary save is the whole test. <c>BlueprintContextFactory</c> attaches the interceptor to every
/// context it builds, exactly as production's <c>AddEventPublishingDbContextFactory</c> does, so a
/// <c>Db.SaveChangesAsync</c> here runs the same code an HTTP request runs. The sanitizer behind it is
/// built over empty configuration - Ganss's defaults - where the application's <c>HtmlSanitizer</c>
/// section only widens the allow-list, so a script tag is stripped identically either way.
/// </remarks>
public class SanitizerInterceptorTests(DatabaseFixture fixture) : DatabaseTestBase(fixture)
{
    /// <summary>Something a client might send that must not survive to a browser.</summary>
    private const string Hostile = "<b>keep</b><script>alert('x')</script>";

    /// <summary>What Ganss's defaults leave of it: the markup allowed, the script element gone.</summary>
    private const string Sanitized = "<b>keep</b>";

    // -------------------------------------------------------------------------------------------------
    // What is sanitized, and when
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// All six attributed properties, on insert. They are spread over five entities and one of them is
    /// not called <c>Description</c>, so this is also the list of the API's string columns that a
    /// reviewer can rely on being safe - and, read the other way, the list of everything that is not.
    /// </remarks>
    [Fact]
    public async Task TheSixAttributedProperties_AreSanitizedOnInsert()
    {
        var msel = BlueprintAppFactory.Msel();
        var organization = BlueprintAppFactory.Organization(msel.Id);
        var team = BlueprintAppFactory.Team(msel.Id);
        var unit = BlueprintAppFactory.Unit();
        var page = BlueprintAppFactory.MselPage(msel.Id);
        var dataField = BlueprintAppFactory.DataField(msel.Id);
        var move = BlueprintAppFactory.Move(msel.Id);
        organization.Description = Hostile;
        team.Description = Hostile;
        unit.Description = Hostile;
        page.Content = Hostile;
        dataField.Description = Hostile;
        move.SituationDescription = Hostile;

        await Seed(msel, organization, team, unit, page, dataField, move);

        await using var stored = NewContext();
        Assert.Equal(Sanitized, (await stored.Organizations.SingleAsync(Ct)).Description);
        Assert.Equal(Sanitized, (await stored.Teams.SingleAsync(Ct)).Description);
        Assert.Equal(Sanitized, (await stored.Units.SingleAsync(Ct)).Description);
        Assert.Equal(Sanitized, (await stored.MselPages.SingleAsync(Ct)).Content);
        Assert.Equal(Sanitized, (await stored.DataFields.SingleAsync(Ct)).Description);
        Assert.Equal(Sanitized, (await stored.Moves.SingleAsync(Ct)).SituationDescription);
    }

    /// <remarks>
    /// And on update, which is the case that matters more: an exercise's descriptions are written once
    /// and edited for weeks.
    /// </remarks>
    [Fact]
    public async Task AnAttributedProperty_IsSanitizedOnUpdateToo()
    {
        var msel = BlueprintAppFactory.Msel();
        var organization = BlueprintAppFactory.Organization(msel.Id);
        organization.Description = "harmless";
        await Seed(msel, organization);

        organization.Description = Hostile;
        await Db.SaveChangesAsync(Ct);

        await using var stored = NewContext();
        Assert.Equal(Sanitized, (await stored.Organizations.SingleAsync(Ct)).Description);
    }

    /// <remarks>
    /// The interceptor writes the sanitized text back onto the entity rather than onto the property
    /// values EF is about to send, so the caller's own object changes under it - which is what makes a
    /// service's own answer safe: every write path in the API returns a view model mapped from the
    /// entity it just saved, after the save.
    /// </remarks>
    [Fact]
    public async Task Sanitizing_RewritesTheCallersOwnObject()
    {
        var msel = BlueprintAppFactory.Msel();
        var organization = BlueprintAppFactory.Organization(msel.Id);
        organization.Description = Hostile;

        await Seed(msel, organization);

        Assert.Equal(Sanitized, organization.Description);
    }

    /// <remarks>
    /// Only <c>Added</c> and <c>Modified</c> entries are walked, so a row on its way out is left as the
    /// caller had it. Nothing depends on this - it is pinned because the guard is the reason a delete
    /// costs no reflection over every tracked entity.
    /// </remarks>
    [Fact]
    public async Task ARowBeingDeleted_IsNotSanitized()
    {
        var msel = BlueprintAppFactory.Msel();
        var organization = BlueprintAppFactory.Organization(msel.Id);
        await Seed(msel, organization);

        organization.Description = Hostile;
        Db.Remove(organization);
        await Db.SaveChangesAsync(Ct);

        Assert.Equal(Hostile, organization.Description);
    }

    /// <remarks>
    /// An <c>Unchanged</c> entry is skipped as well, so a hostile value that reached the database by some
    /// other route - a migration, a restored backup, a column the attribute was added to later - is not
    /// cleaned up by a save that happens to have it tracked. The interceptor guards the write, not the
    /// table.
    /// </remarks>
    [Fact]
    public async Task ARowThatIsMerelyTracked_IsNotSanitized()
    {
        var msel = BlueprintAppFactory.Msel();
        await Seed(msel);
        var tracked = BlueprintAppFactory.Organization(msel.Id);
        tracked.Description = Hostile;
        Db.Attach(tracked);

        await Seed(BlueprintAppFactory.Organization(msel.Id));

        Assert.Equal(EntityState.Unchanged, Db.Entry(tracked).State);
        Assert.Equal(Hostile, tracked.Description);
    }

    /// <remarks>
    /// An unattributed string is stored as sent. This one is a name rather than a description, so the UI
    /// does not render it as HTML - but the attribute is what decides, and the five below are the cases
    /// where that is not so comfortable.
    /// </remarks>
    [Fact]
    public async Task AnUnattributedProperty_IsStoredAsItWasSent()
    {
        var msel = BlueprintAppFactory.Msel();
        var organization = BlueprintAppFactory.Organization(msel.Id);
        organization.Name = Hostile;

        await Seed(msel, organization);

        await using var stored = NewContext();
        Assert.Equal(Hostile, (await stored.Organizations.SingleAsync(Ct)).Name);
    }

    /// <remarks>
    /// BUG: the interceptor does not check for null before sanitizing, and Ganss answers the empty
    /// string, so saving any row with an attributed property unset writes <c>''</c> where the column
    /// would otherwise hold NULL. "Never described" and "described, then cleared" are therefore the same
    /// value, and a query written as <c>Description IS NULL</c> finds neither.
    /// </remarks>
    [Fact]
    public async Task ANullValue_IsStoredAsTheEmptyString()
    {
        var msel = BlueprintAppFactory.Msel();
        var organization = BlueprintAppFactory.Organization(msel.Id);
        organization.Description = null;

        await Seed(msel, organization);

        await using var stored = NewContext();
        Assert.Equal(string.Empty, (await stored.Organizations.SingleAsync(Ct)).Description);
    }

    // -------------------------------------------------------------------------------------------------
    // What is not attributed
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: six properties out of the 43 entities' hundreds of strings, and the set is not the set of
    /// strings a browser renders as HTML. <c>MselEntity.Description</c> is the exercise's own summary;
    /// <c>CardEntity.Description</c> and <c>DataValueEntity.Value</c> are pushed to Gallery, which
    /// renders an article body; <c>MoveEntity.Description</c> sits beside the one move property that
    /// <i>is</i> attributed. So the protection is per-property and looks per-table, which is the way
    /// round that invites a new column to be added unprotected. Deleting a name from the list below is
    /// the test for attributing one.
    /// </remarks>
    [Fact]
    public void TheAttributeIsOnSixProperties_AndNotOnTheStringsThatReachGallery()
    {
        var attributed = AttributedProperties();

        Assert.Equal(
            new[]
            {
                "DataFieldEntity.Description",
                "MoveEntity.SituationDescription",
                "MselPageEntity.Content",
                "OrganizationEntity.Description",
                "TeamEntity.Description",
                "UnitEntity.Description"
            },
            attributed);
        Assert.DoesNotContain("MselEntity.Description", attributed);
        Assert.DoesNotContain("CardEntity.Description", attributed);
        Assert.DoesNotContain("MoveEntity.Description", attributed);
        Assert.DoesNotContain("DataValueEntity.Value", attributed);
    }

    /// <summary>Every <c>[SanitizeHtml]</c> property in the data assembly, as <c>Type.Property</c>.</summary>
    private static List<string> AttributedProperties() =>
        typeof(BlueprintContext).Assembly.GetTypes()
            .SelectMany(x => x.GetProperties())
            .Where(x => Attribute.IsDefined(x, typeof(SanitizeHtmlAttribute)))
            .Select(x => $"{x.DeclaringType.Name}.{x.Name}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
}
