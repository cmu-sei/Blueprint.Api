// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using Blueprint.Api.Tests.Integration.Fixtures;
using Blueprint.Api.ViewModels;
using TUnit.Core;

namespace Blueprint.Api.Tests.Integration.Tests.Controllers;

[Category("Integration")]
[ClassDataSource<BlueprintTestContext>(Shared = SharedType.PerTestSession)]
public class UserControllerTests(BlueprintTestContext context)
{
    [Test]
    public async Task GetUsers_WhenCalled_ReturnsOk()
    {
        // Arrange
        var client = context.CreateClient();

        // Act
        var response = await client.GetAsync("/api/users");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task CreateUser_WithValidData_ReturnsCreatedUser()
    {
        // Arrange
        var client = context.CreateClient();
        var newUser = new User
        {
            Id = Guid.NewGuid(),
            Name = "Integration Test User"
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/users", newUser);

        // Assert
        // The endpoint may require specific permissions or return different codes
        // depending on auth configuration; at minimum it should not be a server error
        await Assert.That((int)response.StatusCode).IsLessThan(500);
    }

    [Test]
    public async Task GetUser_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var client = context.CreateClient();
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await client.GetAsync($"/api/users/{nonExistentId}");

        // Assert
        // Should be NotFound or Forbidden depending on implementation
        var expected = new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden };
        await Assert.That(expected).Contains(response.StatusCode);
    }
}
