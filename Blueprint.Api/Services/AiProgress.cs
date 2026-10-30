// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Hubs;
using Blueprint.Api.Infrastructure.Extensions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Services
{
    /// <summary>What a model call has written so far, while its answer streams in</summary>
    public class AiDraftProgress
    {
        /// <summary>design, injects, article, profile, and the others the usage records name</summary>
        public string Operation { get; set; }
        public Guid? MselId { get; set; }
        public Guid? DraftId { get; set; }
        /// <summary>The characters of the answer so far</summary>
        public int Characters { get; set; }
        /// <summary>The items in each list of the answer so far, by the list's name: moves, injects, citeActions, ...</summary>
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
        public bool Done { get; set; }
    }

    /// <summary>
    /// Sends the drafting user what a long model call has written so far, so the planner sees a three-minute draft
    /// moving. Progress is only news: a report that cannot be sent never fails the call.
    /// </summary>
    public interface IAiProgress
    {
        Task AddAsync(string partialJson, CancellationToken ct);
        Task CompleteAsync(CancellationToken ct);
    }

    public class AiProgress : IAiProgress
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);
        private readonly IHubContext<MainHub> _hub;
        private readonly IAiUsageContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly ILogger<AiProgress> _logger;
        private AiAnswerCounter _counter = new AiAnswerCounter();
        private readonly Stopwatch _sinceSent = new Stopwatch();

        public AiProgress(IHubContext<MainHub> hub, IAiUsageContext context, IPrincipal user, ILogger<AiProgress> logger)
        {
            _hub = hub;
            _context = context;
            _user = user as ClaimsPrincipal;
            _logger = logger;
        }

        public async Task AddAsync(string partialJson, CancellationToken ct)
        {
            _counter.Add(partialJson);
            if (_sinceSent.IsRunning && _sinceSent.Elapsed < Interval)
                return;
            _sinceSent.Restart();
            await SendAsync(false, ct);
        }

        // one request can make more than one call, so each call counts from nothing
        public async Task CompleteAsync(CancellationToken ct)
        {
            if (_counter.Characters > 0)
                await SendAsync(true, ct);
            _counter = new AiAnswerCounter();
            _sinceSent.Reset();
        }

        private async Task SendAsync(bool done, CancellationToken ct)
        {
            try
            {
                // a call with no signed-in user, such as a background step, has no one to tell
                if (_user?.Identity?.IsAuthenticated != true)
                    return;
                var id = _user.GetId();
                // the hub puts each connection in a group named for its user
                await _hub.Clients.Group(id.ToString()).SendAsync(MainHubMethods.AiDraftProgress, new AiDraftProgress
                {
                    Operation = _context.Operation,
                    MselId = _context.MselId,
                    DraftId = _context.DraftId,
                    Characters = _counter.Characters,
                    Counts = new Dictionary<string, int>(_counter.Counts),
                    Done = done
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not send AI draft progress");
            }
        }
    }

    /// <summary>
    /// Counts the items of each top-level list in a JSON object as it streams in, a piece at a time: in
    /// {"moves":[{...},{...}],"injects":[{...}]} two moves and one inject, before the object is complete.
    /// </summary>
    public class AiAnswerCounter
    {
        private int _depth;
        private bool _inString;
        private bool _escaped;
        private bool _readingKey;
        private bool _afterColon;
        private readonly System.Text.StringBuilder _key = new System.Text.StringBuilder();
        private string _topKey;
        private bool _topKeyIsList;

        public int Characters { get; private set; }
        public Dictionary<string, int> Counts { get; } = new Dictionary<string, int>();

        public void Add(string json)
        {
            if (string.IsNullOrEmpty(json))
                return;
            Characters += json.Length;
            foreach (var c in json)
            {
                if (_inString)
                {
                    if (_escaped)
                        _escaped = false;
                    else if (c == '\\')
                        _escaped = true;
                    else if (c == '"')
                    {
                        _inString = false;
                        if (_readingKey)
                        {
                            _topKey = _key.ToString();
                            _readingKey = false;
                        }
                    }
                    else if (_readingKey)
                        _key.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '"':
                        _inString = true;
                        // a string at the top level, before its colon, is a key
                        if (_depth == 1 && !_afterColon)
                        {
                            _readingKey = true;
                            _key.Clear();
                        }
                        break;
                    case ':':
                        if (_depth == 1)
                            _afterColon = true;
                        break;
                    case ',':
                        if (_depth == 1)
                            _afterColon = false;
                        break;
                    case '[':
                        _depth++;
                        if (_depth == 2)
                            _topKeyIsList = true;
                        break;
                    case '{':
                        _depth++;
                        // an object directly inside a top-level list is one of its items
                        if (_depth == 3 && _topKeyIsList && _topKey != null)
                            Counts[_topKey] = Counts.GetValueOrDefault(_topKey) + 1;
                        break;
                    case ']':
                    case '}':
                        _depth--;
                        if (_depth == 1)
                            _topKeyIsList = false;
                        break;
                }
            }
        }
    }
}
