// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

public class IntegrationServiceDispatchTests
{
    [Fact]
    public async Task ProcessTheMsel_posts_an_invalid_dispatch_exception_outside_its_error_reporting()
    {
        var service = new IntegrationService(null, null, null, null, null, null);
        var context = new ExceptionContext();
        var previous = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            typeof(IntegrationService).GetMethod("ProcessTheMsel", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(service, [new object()]);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var error = await context.Exception.Task.WaitAsync(TestContext.Current.CancellationToken);
        var invalid = Assert.IsType<InvalidCastException>(error);
        Assert.Contains("Blueprint.Api.Services.IntegrationInformation", invalid.Message);
    }

    private sealed class ExceptionContext : SynchronizationContext
    {
        public TaskCompletionSource<Exception> Exception { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object state)
        {
            try
            {
                callback(state);
            }
            catch (Exception error)
            {
                Exception.TrySetResult(error);
            }
        }
    }
}
