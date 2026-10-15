// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Filters;
using Blueprint.Api.ViewModels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using NSubstitute;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.Filters;

/// <summary>The two MVC filters <c>Startup</c> adds globally (<c>Startup.cs:152-153</c>): the one that
/// turns an unhandled exception into a response body, and the one that turns an invalid model into a
/// 400.</summary>
public class FilterTests
{
    private static ExceptionContext ExceptionFor(Exception exception) =>
        new(ActionContextFor(new ModelStateDictionary()), []) { Exception = exception };

    private static ActionExecutingContext ActionFor(ModelStateDictionary modelState) =>
        new(ActionContextFor(modelState), [], new Dictionary<string, object>(), controller: null);

    private static ActionContext ActionContextFor(ModelStateDictionary modelState) =>
        new(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), modelState);

    private static JsonExceptionFilter Filter(string environment)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        return new JsonExceptionFilter(env);
    }

    private static ApiError ErrorFrom(ExceptionContext context) =>
        (ApiError)Assert.IsType<JsonResult>(context.Result).Value;

    // -------------------------------------------------------------------------------------------------
    // JsonExceptionFilter
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// Setting <c>context.Result</c> is what marks the exception handled, so this is the assertion that
    /// says a controller throwing does not reach the hosting layer.
    /// </remarks>
    [Fact]
    public void AnException_IsAnswered()
    {
        var context = ExceptionFor(new InvalidOperationException("boom"));

        Filter("Development").OnException(context);

        Assert.Equal(500, Assert.IsType<JsonResult>(context.Result).StatusCode);
        Assert.Equal(500, ErrorFrom(context).Status);
    }

    /// <remarks>
    /// An exception implementing <c>IApiException</c> decides its own status. This is the mechanism
    /// behind every 403 and 404 in the API: the services throw, and nothing catches.
    /// </remarks>
    [Theory]
    [InlineData(typeof(ForbiddenException), (int)HttpStatusCode.Forbidden)]
    [InlineData(typeof(ConflictException), (int)HttpStatusCode.Conflict)]
    public void AnApiException_DecidesItsOwnStatus(Type exception, int expected)
    {
        var context = ExceptionFor((Exception)Activator.CreateInstance(exception, "no"));

        Filter("Development").OnException(context);

        Assert.Equal(expected, ErrorFrom(context).Status);
    }

    [Fact]
    public void AnEntityNotFoundException_Is404WithTheEntityNamed()
    {
        var context = ExceptionFor(new EntityNotFoundException<Msel>());

        Filter("Development").OnException(context);
        var error = ErrorFrom(context);

        Assert.Equal(404, error.Status);
        Assert.Equal("Msel not found", error.Title);
        Assert.Null(error.Detail);
    }

    /// <remarks>
    /// Which is why the suite runs its hosts in Development: the message a failing test needs to read is
    /// in <c>title</c> and the stack trace is in <c>detail</c>.
    /// </remarks>
    [Fact]
    public void AServerErrorInDevelopment_CarriesTheMessageAndTheStackTrace()
    {
        var context = ExceptionFor(Thrown("boom"));

        Filter("Development").OnException(context);
        var error = ErrorFrom(context);

        Assert.Equal("boom", error.Title);
        Assert.Contains(nameof(Thrown), error.Detail);
    }

    /// <summary>A server error in production hides the stack trace and leaks the message.</summary>
    [Fact]
    public void AServerErrorInProduction_HidesTheStackTraceAndLeaksTheMessage()
    {
        var context = ExceptionFor(Thrown("relation \"msels\" does not exist"));

        Filter("Production").OnException(context);
        var error = ErrorFrom(context);

        Assert.Equal("A server error occurred.", error.Title);
        Assert.Equal("relation \"msels\" does not exist", error.Detail);
    }

    /// <remarks>
    /// The non-500 branch is environment-independent, which is right: those messages are written for the
    /// caller. It also means an <c>IApiException</c> is the only way for a service to say anything
    /// deliberate to a client.
    /// </remarks>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void AForbiddenException_SaysTheSameThingInEveryEnvironment(string environment)
    {
        var context = ExceptionFor(new ForbiddenException("you may not"));

        Filter(environment).OnException(context);
        var error = ErrorFrom(context);

        Assert.Equal("you may not", error.Title);
        Assert.Null(error.Detail);
    }

    /// <summary>The error body has no <c>type</c> or <c>instance</c> and is served as <c>application/json</c>.</summary>
    [Fact]
    public void AnErrorBody_NamesNeitherAProblemTypeNorTheRequest()
    {
        var context = ExceptionFor(new InvalidOperationException("boom"));

        Filter("Development").OnException(context);
        var error = ErrorFrom(context);

        Assert.Null(error.Type);
        Assert.Null(error.Instance);
        Assert.Null(Assert.IsType<JsonResult>(context.Result).ContentType);
    }

    // -------------------------------------------------------------------------------------------------
    // ValidateModelStateFilter, driven directly: MVC's own ModelStateInvalidFilter answers every request first.
    // -------------------------------------------------------------------------------------------------

    /// <summary>An invalid model is answered with a 400 carrying the field names.</summary>
    [Fact]
    public void AnInvalidModel_IsA400CarryingTheFieldNames()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("name", "The Name field is required.");

        var context = ActionFor(modelState);
        new ValidateModelStateFilter().OnActionExecuting(context);

        var error = (ApiError)Assert.IsType<BadRequestObjectResult>(context.Result).Value;

        Assert.Equal(400, error.Status);
        Assert.Equal("Invalid Data", error.Title);
        Assert.Equal("name: The Name field is required.", error.Detail);
    }

    /// <summary>Several invalid fields are joined into one newline-separated <c>detail</c>.</summary>
    [Fact]
    public void SeveralInvalidFields_AreJoinedIntoOneString()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("name", "required");
        modelState.AddModelError("mselId", "required");

        var context = ActionFor(modelState);
        new ValidateModelStateFilter().OnActionExecuting(context);

        var error = (ApiError)Assert.IsType<BadRequestObjectResult>(context.Result).Value;

        Assert.Equal("name: required\nmselId: required", error.Detail);
    }

    /// <summary>A model error carrying an exception puts the exception's message in <c>detail</c>.</summary>
    [Fact]
    public void AModelErrorCarryingAnException_LeaksItsMessage()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError(
            "displayOrder",
            new InvalidOperationException("Cannot get the value of a token type 'String' as a number."),
            new EmptyModelMetadataProvider().GetMetadataForType(typeof(int)));

        var context = ActionFor(modelState);
        new ValidateModelStateFilter().OnActionExecuting(context);

        var error = (ApiError)Assert.IsType<BadRequestObjectResult>(context.Result).Value;

        Assert.Equal(
            "displayOrder: Cannot get the value of a token type 'String' as a number.", error.Detail);
    }

    /// <summary>A model error with neither a message nor an exception throws.</summary>
    [Fact]
    public void AModelErrorWithNeitherAMessageNorAnException_Throws()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("name", string.Empty);

        var context = ActionFor(modelState);

        Assert.Throws<NullReferenceException>(
            () => new ValidateModelStateFilter().OnActionExecuting(context));
    }

    /// <remarks>
    /// A valid model leaves the result unset, which is how the action gets to run. Worth pinning because
    /// the filter is global: every request in the API passes through it.
    /// </remarks>
    [Fact]
    public void AValidModel_IsLeftAlone()
    {
        var context = ActionFor(new ModelStateDictionary());

        new ValidateModelStateFilter().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    /// <summary>An exception that has actually been thrown, so that it carries a stack trace.</summary>
    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
