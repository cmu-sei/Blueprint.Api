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

namespace Blueprint.Api.Tests;

/// <summary>
/// The two MVC filters <c>Startup</c> adds globally (<c>Startup.cs:152-153</c>): the one that turns an
/// unhandled exception into a response body, and the one that turns an invalid model into a 400.
/// </summary>
/// <remarks>
/// <para>
/// Driven directly rather than over HTTP, for two reasons that no host test could reach.
/// <see cref="JsonExceptionFilter"/> answers differently in Development and in Production, and the suite
/// runs every host in Development; the Production branch is the one that leaks, so it is the one worth
/// pinning. And <see cref="ValidateModelStateFilter"/> is not reachable over HTTP at all - see the
/// section note below - so direct invocation is the only way to describe what it does.
/// </para>
/// <para>
/// The 404 an <c>EntityNotFoundException</c> produces is asserted many hundreds of times over by the
/// endpoint tests. The 400 is not: <c>ErrorResponseTests</c> is the only file that asserts a 400's body,
/// and what it finds is that no 400 in blueprint has the <c>ApiError</c> shape this filter builds.
/// </para>
/// </remarks>
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

    /// <remarks>
    /// BUG: the Production branch replaces <c>title</c> with "A server error occurred." and then puts the
    /// real exception message in <c>detail</c>, so the message it set out to hide is still on the wire -
    /// one field to the right. Only the stack trace is actually withheld. Every unhandled exception in
    /// blueprint is a raw <c>Exception</c> or an EF or Npgsql one, so what a caller reads is a connection
    /// string fragment, a constraint name or a table name. The fix is to drop <c>error.Detail</c> from
    /// that branch; <c>JsonExceptionFilter</c> has no logger, so log it there at the same time.
    /// </remarks>
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

    /// <remarks>
    /// Nothing sets <c>type</c> or <c>instance</c>, so the body is an RFC 7807 shape with two of its five
    /// members always absent - no problem-type url to look up and no pointer to the request. The content
    /// type is <c>application/json</c> rather than <c>application/problem+json</c> for the same reason:
    /// this is a hand-rolled <c>ApiError</c>, not the framework's <c>ProblemDetails</c>.
    /// </remarks>
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
    // ValidateModelStateFilter - which no request reaches
    //
    // BUG: this filter is dead code in the running application, and everything below describes a
    // component with no callers. BaseController carries [ApiController] (BaseController.cs:11), and every
    // one of the 41 controllers inherits it; nothing in Blueprint.Api configures ApiBehaviorOptions, so
    // SuppressModelStateInvalidFilter is false and MVC installs its own ModelStateInvalidFilter at order
    // -2000. This filter is registered globally at order 0. So MVC's answers first, with a
    // ValidationProblemDetails and application/problem+json, and OnActionExecuting below is never
    // entered with an invalid model state.
    //
    // Which makes the ApiError shape these tests describe - "Invalid Data", the newline-joined detail -
    // something no client has ever received, while the whole API's 400s are RFC 9457 documents nothing
    // in blueprint wrote. ErrorResponseTests asserts what a request actually gets. The fix is a decision
    // rather than a line: delete this filter, or suppress MVC's. Deleting it is the smaller change and
    // loses nothing, the framework's answer being the better of the two.
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// Setting <c>context.Result</c> in <c>OnActionExecuting</c> short-circuits the action, which is what
    /// would keep a bad body from reaching a service if anything reached this filter. The upload routes
    /// look like they rely on it - <c>FileForm.ToUpload</c> carries <c>[Required]</c>, so a multipart POST
    /// with no file part is refused before the service that would have dereferenced null - but the refusal
    /// is MVC's, not this one's. Several earlier commits in this branch recorded that 400 as "the
    /// <c>ApiError</c> shape"; it is <c>ValidationProblemDetails</c>, and <c>ErrorResponseTests</c>
    /// corrects it.
    /// </remarks>
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

    /// <remarks>
    /// Several errors are newline-joined into the one <c>detail</c> string rather than being a list, so a
    /// client cannot show a message against the field it belongs to without splitting on <c>\n</c> and
    /// then on <c>": "</c> - and a message containing either sequence cannot be parsed back at all.
    /// </remarks>
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

    /// <remarks>
    /// A binding failure carries an exception rather than a message, and the filter reads the exception's
    /// message in that case - so a deserialization failure's internals would reach the caller if this
    /// filter answered. MVC's does not read it: an exception-carrying model error becomes the generic
    /// "The supplied value is invalid.", which is why an unparseable Guid tells a client nothing (see
    /// <c>JsonConverterTests.ANullableGuidThatIsNotAGuid_IsAnUnwrappedFormatExceptionNamingNothing</c>).
    /// So the two filters' defects are opposites, and the live one is the one that says too little.
    /// </remarks>
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

    /// <remarks>
    /// BUG, latent: an error with neither a message nor an exception is a <c>NullReferenceException</c>
    /// inside the filter, so the 400 it exists to produce would become a 500 - and, the filter having
    /// already been entered, one that <see cref="JsonExceptionFilter"/> reports as a server error with a
    /// stack trace pointing here rather than at the request. <c>ModelStateDictionary.AddModelError(key,
    /// "")</c> is all it takes, and <c>TryValidateModel</c> with an empty <c>ErrorMessage</c> on a
    /// validation attribute reaches it. Unreachable today for the reason the section note gives, so this
    /// is the shape of thing that would appear the moment somebody suppressed MVC's filter to make the
    /// <c>ApiError</c> shape real - one null-coalesce on <c>x.Exception?.Message</c> ahead of that.
    /// </remarks>
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
