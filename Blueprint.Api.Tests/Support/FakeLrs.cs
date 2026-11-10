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
/// An LRS on loopback for the one route that reads from the LRS on the request thread,
/// <c>GetStatementsAsync</c>. <c>XApiService</c> news up its own <c>HttpClient</c>, so a listening socket is
/// the only seam it has. It answers every request with the same statement list, or with whatever a
/// responder says for each request, and keeps every request it got.
/// </summary>
public sealed class FakeLrs : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<Request, Response> _respond;
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>An LRS that answers every request with <paramref name="body"/>.</summary>
    public FakeLrs(string body) : this(_ => new Response(body))
    {
    }

    /// <summary>
    /// An LRS that answers each request with what <paramref name="respond"/> returns for it, which is how a
    /// test serves pages: the first query by its registration, the rest by the <c>more</c> links it handed out.
    /// </summary>
    public FakeLrs(Func<Request, Response> respond)
    {
        _respond = respond;
        var port = FreePort();
        Origin = $"http://127.0.0.1:{port}";
        Endpoint = $"{Origin}/xapi";
        _listener.Prefixes.Add($"{Origin}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    /// <summary>The scheme, host and port, for building an absolute <c>more</c> link.</summary>
    public string Origin { get; }

    /// <summary>The value for <c>XApiOptions.Endpoint</c>.</summary>
    public string Endpoint { get; }

    /// <summary>Every request so far, in the order they came in.</summary>
    public IReadOnlyList<Request> Requests => [.. _requests];

    /// <summary>The <c>registration</c> of every statement query received, in order.</summary>
    public IReadOnlyList<string> Registrations => _requests
        .Select(x => x.Query("registration"))
        .ToList();

    /// <summary>
    /// One request: its path and query as sent (<c>/xapi/statements?registration=...</c>) and the two
    /// headers an LRS needs on every page.
    /// </summary>
    public sealed record Request(string PathAndQuery, string Authorization, string Version)
    {
        /// <summary>A query parameter's value, or null.</summary>
        public string Query(string name) =>
            System.Web.HttpUtility.ParseQueryString(new Uri("http://lrs" + PathAndQuery).Query)[name];
    }

    /// <summary>What the LRS answers: a status and a body.</summary>
    /// <summary>What to answer. <paramref name="Drop"/> closes the connection without an answer, as a host that went away does.</summary>
    public sealed record Response(string Body, int Status = 200, bool Drop = false);

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

            var request = new Request(
                context.Request.RawUrl,
                context.Request.Headers["Authorization"],
                context.Request.Headers["X-Experience-API-Version"]);
            _requests.Enqueue(request);

            Response answer;
            try
            {
                answer = _respond(request);
            }
            catch (Exception ex)
            {
                answer = new Response(ex.Message, 500);
            }

            if (answer.Drop)
            {
                context.Response.Abort();
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(answer.Body ?? "");
            context.Response.StatusCode = answer.Status;
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
