// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net;
using Blueprint.Api.Tests.Integration.Fixtures;
using TUnit.Core;

namespace Blueprint.Api.Tests.Integration.Tests.Controllers;

[Category("Integration")]
[ClassDataSource<BlueprintTestContext>(Shared = SharedType.PerTestSession)]
public class HealthCheckTests(BlueprintTestContext context)
{
    [Test]
    public async Task GetVersion_WhenCalled_ReturnsOk()
    {
        // Arrange
        var client = context.CreateClient();

        // Act
        var response = await client.GetAsync("/api/version");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        await Assert.That(string.IsNullOrWhiteSpace(content)).IsFalse();
    }

    [Test]
    public async Task GetLiveliness_WhenCalled_ReturnsHealthStatus()
    {
        // Arrange
        var client = context.CreateClient();

        // Act
        var response = await client.GetAsync("/api/health/live");

        // Assert
        // Health check may return OK or ServiceUnavailable depending on configured checks,
        // but it should respond without error
        var validStatuses = new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable };
        await Assert.That(validStatuses).Contains(response.StatusCode);
    }

    [Test]
    public async Task GetReadiness_WhenCalled_ReturnsHealthStatus()
    {
        // Arrange
        var client = context.CreateClient();

        // Act
        var response = await client.GetAsync("/api/health/ready");

        // Assert
        var validStatuses = new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable };
        await Assert.That(validStatuses).Contains(response.StatusCode);
    }
}
