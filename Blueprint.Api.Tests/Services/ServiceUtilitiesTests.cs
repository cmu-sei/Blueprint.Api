// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><see cref="ServiceUtilities.IsDateTimeValue"/>, which decides what a DateTime field may hold.</summary>
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
}
