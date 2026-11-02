// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary><c>AttackTechniqueCountermeasureService.ParentExternalId</c> - the technique a sub-technique falls back to.</summary>
public class AttackTechniqueCountermeasureServiceTests
{
    [Theory]
    [InlineData("T1003.005", "T1003")]
    [InlineData("AML.T0000.000", "AML.T0000")]
    [InlineData("EX-0001.01", "EX-0001")]
    [InlineData("T1003", null)]
    [InlineData("AML.T0000", null)]
    [InlineData("T0800", null)]
    [InlineData("T1003.", null)]
    [InlineData(".005", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParentExternalId_IsTheIdBeforeANumericLastPart(string externalId, string expected)
    {
        Assert.Equal(expected, AttackTechniqueCountermeasureService.ParentExternalId(externalId));
    }
}
