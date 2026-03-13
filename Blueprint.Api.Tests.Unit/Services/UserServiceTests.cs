// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Security.Claims;
using System.Security.Principal;
using AutoFixture;
using AutoFixture.AutoFakeItEasy;
using AutoMapper;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Shared.Fixtures;
using Crucible.Common.Testing.Fixtures;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Blueprint.Api.Tests.Unit.Services;

[Trait("Category", "Unit")]
public class UserServiceTests
{
    private readonly IFixture _fixture;
    private readonly IMapper _mapper;
    private readonly IUserClaimsService _fakeUserClaimsService;
    private readonly ILogger<IUserService> _fakeLogger;
    private readonly Guid _currentUserId;
    private readonly ClaimsPrincipal _claimsPrincipal;

    public UserServiceTests()
    {
        _fixture = new Fixture()
            .Customize(new AutoFakeItEasyCustomization())
            .Customize(new BlueprintCustomization());

        // Use real AutoMapper configuration for tests
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<UserEntity, Blueprint.Api.ViewModels.User>();
            cfg.CreateMap<Blueprint.Api.ViewModels.User, UserEntity>();
        });
        _mapper = config.CreateMapper();

        _fakeUserClaimsService = A.Fake<IUserClaimsService>();
        _fakeLogger = A.Fake<ILogger<IUserService>>();

        _currentUserId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new("sub", _currentUserId.ToString()),
            new(ClaimTypes.NameIdentifier, _currentUserId.ToString())
        };
        _claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private UserService CreateService(BlueprintContext context) => new(
        context,
        _claimsPrincipal,
        _fakeUserClaimsService,
        _fakeLogger,
        _mapper);

    [Fact]
    public async Task GetAsync_ById_WithSystemPermission_ReturnsUser()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var userId = Guid.NewGuid();
        var userEntity = _fixture.Build<UserEntity>()
            .Without(u => u.Role)
            .Without(u => u.TeamUsers)
            .Without(u => u.UnitUsers)
            .Without(u => u.GroupMemberships)
            .With(u => u.Id, userId)
            .Create();

        context.Users.Add(userEntity);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        // Act
        var result = await service.GetAsync(userId, true, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(userId);
    }

    [Fact]
    public async Task GetAsync_ById_WithoutPermission_ThrowsForbidden()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var otherUserId = Guid.NewGuid();
        var service = CreateService(context);

        // Act & Assert
        await Should.ThrowAsync<ForbiddenException>(
            service.GetAsync(otherUserId, false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithValidUser_CreatesAndReturnsUser()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var inputUser = new Blueprint.Api.ViewModels.User
        {
            Id = Guid.NewGuid(),
            Name = "Test User"
        };

        var service = CreateService(context);

        // Act
        var result = await service.CreateAsync(inputUser, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Test User");
        var createdEntity = await context.Users.FindAsync(inputUser.Id);
        createdEntity.ShouldNotBeNull();
        createdEntity.Name.ShouldBe("Test User");
    }
}
