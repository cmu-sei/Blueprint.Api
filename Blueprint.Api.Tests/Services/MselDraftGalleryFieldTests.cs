// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>MselDraftRules.AddGalleryFields</c> - a drafted MSEL that uses Gallery gets the columns
/// its articles are pushed from, even when the template never mapped any.</summary>
public class MselDraftGalleryFieldTests
{
    [Fact]
    public void AddGalleryFields_MapsTheTemplatesOwnColumnsFirst()
    {
        var msel = StandardMsel(useGallery: true);

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal("Name", Field(msel, "Title").GalleryArticleParameter);
        Assert.Equal("Description", Field(msel, "Description").GalleryArticleParameter);
        Assert.Null(Field(msel, "Details").GalleryArticleParameter);
        Assert.Equal("FromOrg", Field(msel, "From Org").GalleryArticleParameter);
        Assert.Equal("ToOrg", Field(msel, "To Org").GalleryArticleParameter);
    }

    [Fact]
    public void AddGalleryFields_AddsTheColumnsTheTemplateLacks()
    {
        var msel = StandardMsel(useGallery: true);

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal(DataFieldType.String, Field(msel, "Summary").DataType);
        Assert.Equal(DataFieldType.SourceType, Field(msel, "Source Type").DataType);
        Assert.Equal(DataFieldType.String, Field(msel, "Source Name").DataType);
        Assert.Equal(DataFieldType.DateTime, Field(msel, "Date Posted").DataType);
        Assert.Equal(DataFieldType.Card, Field(msel, "Card").DataType);
        Assert.Equal(
            ["CardId", "DatePosted", "Description", "FromOrg", "Name", "SourceName", "SourceType", "Summary", "ToOrg"],
            msel.DataFields.Select(f => f.GalleryArticleParameter).Where(p => p != null).Order().ToArray());
    }

    [Fact]
    public void AddGalleryFields_PutsTheNewColumnsAfterTheTemplates()
    {
        var msel = StandardMsel(useGallery: true);

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal(5, Field(msel, "Summary").DisplayOrder);
        Assert.Equal(9, Field(msel, "Card").DisplayOrder);
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
        // a Summary column of the wrong type cannot carry the article summary
        msel.DataFields.Add(new DataFieldEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "Summary", DataType = DataFieldType.Html, DisplayOrder = 5 });

        MselDraftRules.AddGalleryFields(msel);

        Assert.Null(Field(msel, "Summary").GalleryArticleParameter);
        Assert.Equal("Summary", Field(msel, "Gallery Summary").GalleryArticleParameter);
    }

    [Fact]
    public void AddGalleryFields_WithoutGallery_ChangesNothing()
    {
        var msel = StandardMsel(useGallery: false);

        MselDraftRules.AddGalleryFields(msel);

        Assert.Equal(4, msel.DataFields.Count);
        Assert.All(msel.DataFields, f => Assert.Null(f.GalleryArticleParameter));
    }

    private static MselEntity StandardMsel(bool useGallery)
    {
        var msel = new MselEntity { Id = Guid.NewGuid(), UseGallery = useGallery };
        foreach (var (name, type, order) in new[]
        {
            ("Title", DataFieldType.String, 1),
            ("Description", DataFieldType.String, 2),
            ("Details", DataFieldType.String, 4),
            ("From Org", DataFieldType.Organization, 3)
        })
            msel.DataFields.Add(new DataFieldEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = name, DataType = type, DisplayOrder = order });
        if (useGallery)
            msel.DataFields.Add(new DataFieldEntity { Id = Guid.NewGuid(), MselId = msel.Id, Name = "To Org", DataType = DataFieldType.TeamsMultiple, DisplayOrder = 4 });
        return msel;
    }

    private static DataFieldEntity Field(MselEntity msel, string name) =>
        msel.DataFields.Single(f => f.Name == name);
}
