// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Blueprint.Api.Tests.Support;

/// <summary>
/// An LRS on loopback that answers every request with the same statement list, for the one route that reads
/// from the LRS on the request thread, <c>GetStatementsAsync</c>. <c>XApiService</c> news up its own
/// <c>HttpClient</c>, so a listening socket is the only seam it has.
/// </summary>
public sealed class FakeLrs : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _body;
    private readonly CancellationTokenSource _stop = new();

    public FakeLrs(string body)
    {
        _body = body;
        var port = FreePort();
        Endpoint = $"http://127.0.0.1:{port}/xapi";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    /// <summary>The value for <c>XApiOptions.Endpoint</c>.</summary>
    public string Endpoint { get; }

    private readonly ConcurrentQueue<Uri> _requests = new();

    /// <summary>The <c>registration</c> of every statement query received, in order.</summary>
    public IReadOnlyList<string> Registrations => _requests
        .Select(x => System.Web.HttpUtility.ParseQueryString(x.Query)["registration"])
        .ToList();

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _requests.Enqueue(context.Request.Url);
            var bytes = Encoding.UTF8.GetBytes(_body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}
