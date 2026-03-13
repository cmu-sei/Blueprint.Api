// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net;
using Blueprint.Api.Tests.Integration.Fixtures;
using Shouldly;
using Xunit;

namespace Blueprint.Api.Tests.Integration.Tests.Controllers;

public class HealthCheckTests : IClassFixture<BlueprintTestContext>
{
    private readonly BlueprintTestContext _testContext;

    public HealthCheckTests(BlueprintTestContext testContext)
    {
        _testContext = testContext;
    }

    [Fact]
    public async Task GetVersion_ReturnsOk()
    {
        // Arrange
        var client = _testContext.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetLiveliness_ReturnsHealthStatus()
    {
        // Arrange
        var client = _testContext.CreateClient();

        // Act
        var response = await client.GetAsync("/api/health/live");

        // Assert
        // Health check may return OK or ServiceUnavailable depending on configured checks,
        // but it should respond without error
        var validStatuses = new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable };
        validStatuses.ShouldContain(response.StatusCode);
    }

    [Fact]
    public async Task GetReadiness_ReturnsHealthStatus()
    {
        // Arrange
        var client = _testContext.CreateClient();

        // Act
        var response = await client.GetAsync("/api/health/ready");

        // Assert
        var validStatuses = new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable };
        validStatuses.ShouldContain(response.StatusCode);
    }
}
