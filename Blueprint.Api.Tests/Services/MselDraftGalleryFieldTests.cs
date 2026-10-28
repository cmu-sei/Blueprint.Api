// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>MselDraftRules.AddGalleryFields</c> - a drafted MSEL that uses Gallery gets the columns
/// its articles are pushed from, even when the template never mapped any, mapped as blueprint-ttx maps them.</summary>
public class MselDraftGalleryFieldTests
{
    [Fact]
    public void AddGalleryFields_MapsTheStandardMselColumnsAsArticleParts()
    {
        var msel = StandardMsel(useGallery: true);

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal("Name", Field(msel, "Title").GalleryArticleParameter);
        Assert.Equal("Summary", Field(msel, "Description").GalleryArticleParameter);
        Assert.Equal("Description", Field(msel, "Details").GalleryArticleParameter);
        Assert.Equal("DatePosted", Field(msel, "Simulated Time").GalleryArticleParameter);
        Assert.Equal("FromOrg", Field(msel, "From Org").GalleryArticleParameter);
        Assert.Equal("ToOrg", Field(msel, "To Org").GalleryArticleParameter);
        Assert.Null(Field(msel, "Delivery Time").GalleryArticleParameter);
    }

    [Fact]
    public void AddGalleryFields_AddsTheColumnsTheTemplateLacks()
    {
        var msel = StandardMsel(useGallery: true);

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal(DataFieldType.SourceType, Field(msel, "SourceType").DataType);
        Assert.Equal(DataFieldType.String, Field(msel, "Source Name").DataType);
        Assert.Equal(DataFieldType.Card, Field(msel, "Card").DataType);
        Assert.Equal(
            ["CardId", "DatePosted", "Description", "FromOrg", "Name", "SourceName", "SourceType", "Summary", "ToOrg"],
            msel.DataFields.Select(f => f.GalleryArticleParameter).Where(p => p != null).Order().ToArray());
    }

    [Fact]
    public void AddGalleryFields_AddsAnHtmlBodyWhenTheTemplateHasNoDetails()
    {
        var msel = StandardMsel(useGallery: true);
        msel.DataFields.Remove(Field(msel, "Details"));

        MselDraftRules.AddGalleryFields(msel);

        var details = Field(msel, "Details");
        Assert.Equal(DataFieldType.Html, details.DataType);
        Assert.Equal("Description", details.GalleryArticleParameter);
    }

    [Fact]
    public void AddGalleryFields_PutsTheNewColumnsAfterTheTemplates()
    {
        var msel = StandardMsel(useGallery: true);

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal(9, Field(msel, "SourceType").DisplayOrder);
        Assert.Equal(11, Field(msel, "Card").DisplayOrder);
        Assert.All(msel.DataFields, f => Assert.Equal(msel.Id, f.MselId));
    }

    [Fact]
    public void AddGalleryFields_RunTwice_AddsNothingMore()
    {
        var msel = StandardMsel(useGallery: true);

        MselDraftRules.AddGalleryFields(msel);
        var count = msel.DataFields.Count;
        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal(count, msel.DataFields.Count);
    }

    [Fact]
    public void AddGalleryFields_RenamesANewColumnWhoseNameIsTaken()
    {
        var msel = StandardMsel(useGallery: true);
        // a Card column of the wrong type cannot carry the article's card
        msel.DataFields.Add(new DataFieldEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Card", DataType = DataFieldType.String, DisplayOrder = 9 });

        MselDraftRules.AddGalleryFields(msel);

        Assert.Null(Field(msel, "Card").GalleryArticleParameter);
        Assert.Equal("CardId", Field(msel, "Gallery Card").GalleryArticleParameter);
    }

    [Fact]
    public void AddGalleryFields_KeepsTheTemplatesOwnMapping()
    {
        var msel = StandardMsel(useGallery: true);
        Field(msel, "Details").GalleryArticleParameter = "Summary";

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal("Summary", Field(msel, "Details").GalleryArticleParameter);
        Assert.Null(Field(msel, "Description").GalleryArticleParameter);
        Assert.Equal(DataFieldType.Html, msel.DataFields.Single(f => f.GalleryArticleParameter == "Description").DataType);
    }

    [Fact]
    public void AddGalleryFields_WithoutGallery_ChangesNothing()
    {
        var msel = StandardMsel(useGallery: false);
        var count = msel.DataFields.Count;

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal(count, msel.DataFields.Count);
        Assert.All(msel.DataFields, f => Assert.Null(f.GalleryArticleParameter));
    }

    // the columns of the seeded Standard MSEL template that matter here
    private static MselEntity StandardMsel(bool useGallery)
    {
        var msel = new MselEntity { Id = Guid.NewGuid(), UseGallery = useGallery };
        foreach (var (name, type, order) in new[]
        {
            ("Delivery Time", DataFieldType.DateTime, 1),
            ("Simulated Time", DataFieldType.DateTime, 2),
            ("Title", DataFieldType.String, 3),
            ("Description", DataFieldType.String, 4),
            ("From Org", DataFieldType.Organization, 5),
            ("To Org", DataFieldType.TeamsMultiple, 6),
            ("Details", DataFieldType.String, 7),
            ("Expected Actions", DataFieldType.String, 8)
        })
            msel.DataFields.Add(new DataFieldEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = name, DataType = type, DisplayOrder = order });
        return msel;
    }

    private static DataFieldEntity Field(MselEntity msel, string name) =>
        msel.DataFields.Single(f => f.Name == name);
}
