// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Security.Claims;
using AutoFixture;
using AutoFixture.AutoFakeItEasy;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Shared.Fixtures;
using Cite.Api.Client;
using Crucible.Common.Testing.Fixtures;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Blueprint.Api.Tests.Unit.Services;

[Trait("Category", "Unit")]
public class MselServiceTests
{
    private readonly IFixture _fixture;
    private readonly AutoMapper.IMapper _fakeMapper;

    public MselServiceTests()
    {
        _fixture = new Fixture()
            .Customize(new AutoFakeItEasyCustomization())
            .Customize(new BlueprintCustomization());
        _fakeMapper = A.Fake<AutoMapper.IMapper>();
    }

    [Fact]
    public async Task GetAsync_WithValidId_ReturnsMappedMsel()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var mselEntity = _fixture.Create<MselEntity>();
        var expectedMsel = new Blueprint.Api.ViewModels.Msel
        {
            Id = mselEntity.Id,
            Name = mselEntity.Name,
            Description = mselEntity.Description
        };

        context.Msels.Add(mselEntity);
        await context.SaveChangesAsync();

        A.CallTo(() => _fakeMapper.Map<Blueprint.Api.ViewModels.Msel>(A<MselEntity>._)).Returns(expectedMsel);

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var service = new MselService(
            context,
            A.Fake<ClientOptions>(),
            A.Fake<IScenarioEventService>(),
            A.Fake<IIntegrationQueue>(),
            A.Fake<IPlayerService>(),
            A.Fake<IJoinQueue>(),
            principal,
            A.Fake<ILogger<MselService>>(),
            _fakeMapper,
            A.Fake<IXApiService>(),
            A.Fake<ICiteApiClient>());

        // Act
        var result = await service.GetAsync(mselEntity.Id, true, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(mselEntity.Id);
    }

    [Fact]
    public async Task DeleteAsync_WithExistingId_ReturnsTrue()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var mselEntity = _fixture.Create<MselEntity>();

        context.Msels.Add(mselEntity);
        await context.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var service = new MselService(
            context,
            A.Fake<ClientOptions>(),
            A.Fake<IScenarioEventService>(),
            A.Fake<IIntegrationQueue>(),
            A.Fake<IPlayerService>(),
            A.Fake<IJoinQueue>(),
            principal,
            A.Fake<ILogger<MselService>>(),
            _fakeMapper,
            A.Fake<IXApiService>(),
            A.Fake<ICiteApiClient>());

        // Act
        var result = await service.DeleteAsync(mselEntity.Id, true, CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        var deletedEntity = await context.Msels.FindAsync(mselEntity.Id);
        deletedEntity.ShouldBeNull();
    }

    [Fact]
    public void FilterUserMselRolesByUser_WithSpecificUserId_FiltersCorrectly()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var mselEntity = _fixture.Create<MselEntity>();

        var userRole = new UserMselRoleEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MselId = mselEntity.Id,
            Role = MselRole.Owner
        };
        var otherRole = new UserMselRoleEntity
        {
            Id = Guid.NewGuid(),
            UserId = otherUserId,
            MselId = mselEntity.Id,
            Role = MselRole.Viewer
        };
        mselEntity.UserMselRoles = new List<UserMselRoleEntity> { userRole, otherRole };

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var service = new MselService(
            context,
            A.Fake<ClientOptions>(),
            A.Fake<IScenarioEventService>(),
            A.Fake<IIntegrationQueue>(),
            A.Fake<IPlayerService>(),
            A.Fake<IJoinQueue>(),
            principal,
            A.Fake<ILogger<MselService>>(),
            _fakeMapper,
            A.Fake<IXApiService>(),
            A.Fake<ICiteApiClient>());

        // Act
        service.FilterUserMselRolesByUser(userId, mselEntity);

        // Assert
        mselEntity.UserMselRoles.ShouldAllBe(r => r.UserId == userId);
        mselEntity.UserMselRoles.Count.ShouldBe(1);
    }
}
