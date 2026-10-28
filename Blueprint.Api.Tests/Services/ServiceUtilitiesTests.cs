// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Globalization;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <see cref="ServiceUtilities.IsDateTimeValue"/>, which decides what a DateTime field may hold, and the
/// helpers the xlsx export writes cells with.
/// </summary>
public class ServiceUtilitiesTests
{
    /// <summary>The forms the UI's date picker saves and the xlsx import writes.</summary>
    [Theory]
    [InlineData("2026-10-12T16:00:00.000Z")]
    [InlineData("2026-10-12T16:00:00Z")]
    [InlineData("2026-10-12T12:00:00-04:00")]
    [InlineData("2026-10-12")]
    [InlineData("3/15/2023")]
    [InlineData("3/15/2023 18:00")]
    [InlineData(" 3/15/2023 ")]
    public void IsDateTimeValue_ForADate_IsTrue(string value)
    {
        Assert.True(ServiceUtilities.IsDateTimeValue(value));
    }

    /// <summary>
    /// Text the UI can't show as a date, including a bare time and numbers .NET's lenient parse would
    /// take as one.
    /// </summary>
    [Theory]
    [InlineData("TEAM1")]
    [InlineData("10:00")]
    [InlineData("1.5")]
    [InlineData("3-4")]
    [InlineData("45000")]
    [InlineData("")]
    [InlineData(null)]
    public void IsDateTimeValue_ForTextThatIsNotADate_IsFalse(string value)
    {
        Assert.False(ServiceUtilities.IsDateTimeValue(value));
    }

    /// <summary>
    /// The calendar dates the UI's check also refuses, which a lenient parse would roll over to March.
    /// </summary>
    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2/30/2023")]
    [InlineData("2026-10-12T16:00:00.12345678Z")]
    public void IsDateTimeValue_ForADateNotOnTheCalendar_IsFalse(string value)
    {
        Assert.False(ServiceUtilities.IsDateTimeValue(value));
    }

    /// <summary>An imported M/d/yyyy value, with its time, as a date cell holds it; ISO 8601 is left as it is.</summary>
    [Theory]
    [InlineData("3/15/2023", "2023-03-15T00:00:00")]
    [InlineData("3/15/2023 18:00", "2023-03-15T18:00:00")]
    [InlineData("3/15/2023 6:00 PM", "2023-03-15T18:00:00")]
    [InlineData("2026-10-12T16:00:00.000Z", "2026-10-12T16:00:00.000Z")]
    public void ToCellDateTime_WritesTheDateAndTime(string value, string expected)
    {
        Assert.Equal(expected, ServiceUtilities.ToCellDateTime(value));
    }

    /// <summary>Whether a value can be written as a cell of its Data Field's type rather than as text.</summary>
    [Theory]
    [InlineData(DataFieldType.DateTime, "3/15/2023", true)]
    [InlineData(DataFieldType.DateTime, "TEAM1", false)]
    [InlineData(DataFieldType.Integer, "42", true)]
    [InlineData(DataFieldType.Integer, "TEAM1", false)]
    [InlineData(DataFieldType.Double, "1.5", true)]
    [InlineData(DataFieldType.Double, "1,5", false)]
    [InlineData(DataFieldType.Boolean, "true", true)]
    [InlineData(DataFieldType.Boolean, "1", true)]
    [InlineData(DataFieldType.Boolean, "TEAM1", false)]
    [InlineData(DataFieldType.String, "TEAM1", true)]
    public void FitsCellType_IsTrueOnlyForAValueOfTheType(DataFieldType dataType, string value, bool fits)
    {
        Assert.Equal(fits, ServiceUtilities.FitsCellType(dataType, value));
    }

    /// <summary>
    /// A cell's metadata keeps four parts on a server whose regional settings write a decimal comma, so
    /// the type stays in the last place where the export and the import read it.
    /// </summary>
    [Fact]
    public void CellMetadata_UnderADecimalCommaCulture_WritesTheTintWithAPoint()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("FF4472C4,0.5,normal,40", ServiceUtilities.CellMetadata("FF4472C4", 0.5, "normal", DataFieldType.DateTime));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
