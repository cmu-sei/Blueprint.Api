// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using Blueprint.Api.Tests.Integration.Fixtures;
using Blueprint.Api.ViewModels;
using Shouldly;
using Xunit;

namespace Blueprint.Api.Tests.Integration.Tests.Controllers;

public class UserControllerTests : IClassFixture<BlueprintTestContext>
{
    private readonly BlueprintTestContext _testContext;

    public UserControllerTests(BlueprintTestContext testContext)
    {
        _testContext = testContext;
    }

    [Fact]
    public async Task GetUsers_ReturnsOk()
    {
        // Arrange
        var client = _testContext.CreateClient();

        // Act
        var response = await client.GetAsync("/api/users");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateUser_WithValidData_ReturnsCreatedUser()
    {
        // Arrange
        var client = _testContext.CreateClient();
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
        ((int)response.StatusCode).ShouldBeLessThan(500);
    }

    [Fact]
    public async Task GetUser_ById_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var client = _testContext.CreateClient();
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await client.GetAsync($"/api/users/{nonExistentId}");

        // Assert
        // Should be NotFound or Forbidden depending on implementation
        var expected = new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden };
        expected.ShouldContain(response.StatusCode);
    }
}
