// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Security.Claims;
using System.Security.Principal;
using AutoFixture;
using AutoFixture.AutoFakeItEasy;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Shared.Fixtures;
using Blueprint.Api.ViewModels;
using Crucible.Common.Testing.Fixtures;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Blueprint.Api.Tests.Unit.Services;

[Trait("Category", "Unit")]
public class InjectServiceTests
{
    private readonly IFixture _fixture;
    private readonly AutoMapper.IMapper _fakeMapper;
    private readonly IPrincipal _fakeUser;

    public InjectServiceTests()
    {
        _fixture = new Fixture()
            .Customize(new AutoFakeItEasyCustomization())
            .Customize(new BlueprintCustomization());
        _fakeMapper = A.Fake<AutoMapper.IMapper>();

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        _fakeUser = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task GetAsync_WithValidId_ReturnsMappedInject()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var injectEntity = _fixture.Create<InjectEntity>();
        var expectedInject = new Injectm { Id = injectEntity.Id, Name = injectEntity.Name };

        context.Injects.Add(injectEntity);
        await context.SaveChangesAsync();

        A.CallTo(() => _fakeMapper.Map<Injectm>(A<InjectEntity>._)).Returns(expectedInject);

        var service = new InjectService(
            context,
            _fakeUser,
            _fakeMapper,
            new DatabaseOptions());

        // Act
        var result = await service.GetAsync(injectEntity.Id, true, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(injectEntity.Id);
    }

    [Fact]
    public async Task GetByInjectTypeAsync_WithMatchingType_ReturnsFilteredInjects()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var injectTypeId = Guid.NewGuid();
        var matchingInject = _fixture.Build<InjectEntity>()
            .Without(x => x.InjectType)
            .Without(x => x.RequiresInject)
            .Without(x => x.CatalogInjects)
            .With(x => x.InjectTypeId, injectTypeId)
            .With(x => x.DataValues, new List<DataValueEntity>())
            .Create();
        var otherInject = _fixture.Build<InjectEntity>()
            .Without(x => x.InjectType)
            .Without(x => x.RequiresInject)
            .Without(x => x.CatalogInjects)
            .With(x => x.InjectTypeId, Guid.NewGuid())
            .With(x => x.DataValues, new List<DataValueEntity>())
            .Create();

        context.Injects.AddRange(matchingInject, otherInject);
        await context.SaveChangesAsync();

        A.CallTo(() => _fakeMapper.Map<IEnumerable<Injectm>>(A<List<InjectEntity>>._))
            .Returns(new List<Injectm> { new() { Id = matchingInject.Id } });

        var service = new InjectService(
            context,
            _fakeUser,
            _fakeMapper,
            new DatabaseOptions());

        // Act
        var result = await service.GetByInjectTypeAsync(injectTypeId, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Count().ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAsync_WithExistingId_ReturnsTrue()
    {
        // Arrange
        using var context = TestDbContextFactory.Create<BlueprintContext>();
        var injectEntity = _fixture.Create<InjectEntity>();

        context.Injects.Add(injectEntity);
        await context.SaveChangesAsync();

        var service = new InjectService(
            context,
            _fakeUser,
            _fakeMapper,
            new DatabaseOptions());

        // Act
        var result = await service.DeleteAsync(injectEntity.Id, CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        var deletedEntity = await context.Injects.FindAsync(injectEntity.Id);
        deletedEntity.ShouldBeNull();
    }
}
