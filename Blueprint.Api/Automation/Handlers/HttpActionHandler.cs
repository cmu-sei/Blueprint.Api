// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Microsoft.Extensions.Options;

namespace Blueprint.Api.Automation.Handlers
{
    /// <summary>
    /// http_get, http_post, http_put and http_delete, to the hosts <see cref="IOutboundRequestGuard"/> allows. The
    /// service account's token goes only to the hosts named for it, and only when the task sets no Authorization
    /// header of its own. A response that is not a 2xx fails the task.
    /// </summary>
    public class HttpActionHandler : IAutomationActionHandler
    {
        private static readonly Regex Marker = new(@"\{(\w+)\}", RegexOptions.Compiled);
        private readonly IOutboundRequestGuard _guard;
        private readonly IServiceAccountTokenProvider _tokens;
        private readonly IOptionsMonitor<AutomationOptions> _options;

        public HttpActionHandler(IOutboundRequestGuard guard, IServiceAccountTokenProvider tokens, IOptionsMonitor<AutomationOptions> options)
        {
            _guard = guard;
            _tokens = tokens;
            _options = options;
        }

        public IReadOnlyCollection<AutomationAction> Actions { get; } = new[]
        {
            AutomationAction.http_get, AutomationAction.http_post, AutomationAction.http_put, AutomationAction.http_delete,
        };

        public async Task<ActionOutcome> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
        {
            var uri = _guard.CheckHttpUrl(context.Get("Url"));
            var method = context.Action switch
            {
                AutomationAction.http_post => HttpMethod.Post,
                AutomationAction.http_put => HttpMethod.Put,
                AutomationAction.http_delete => HttpMethod.Delete,
                _ => HttpMethod.Get,
            };
            using var request = new HttpRequestMessage(method, uri);
            if (method == HttpMethod.Post || method == HttpMethod.Put)
            {
                request.Content = new StringContent(context.Get("Body") ?? string.Empty, Encoding.UTF8, "application/json");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
            foreach (var (name, value) in Headers(context.Get("Headers")))
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            if (request.Headers.Authorization == null && _guard.SendsServiceToken(uri))
            {
                var token = await _tokens.GetTokenAsync(ct);
                request.Headers.Authorization = new AuthenticationHeaderValue(token.TokenType ?? "Bearer", token.AccessToken);
            }

            using var client = _guard.CreateHttpClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await ReadAsync(response, _options.CurrentValue.Http.MaxResponseBytes, ct);
            if (!response.IsSuccessStatusCode)
                return new ActionOutcome($"{(int)response.StatusCode} {response.ReasonPhrase}\n{body}".TrimEnd(), false);
            return new ActionOutcome(body);
        }

        /// <summary>The task's headers, a JSON object of names and values, each <c>{name}</c> replaced from Automation:Http:HeaderReplacements.</summary>
        private IEnumerable<KeyValuePair<string, string>> Headers(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Enumerable.Empty<KeyValuePair<string, string>>();
            Dictionary<string, string> headers;
            try
            {
                headers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
            catch (JsonException)
            {
                throw new AutomationConfigurationException("The task's headers are not a JSON object of names and values.");
            }
            var replacements = _options.CurrentValue.Http.HeaderReplacements ?? new();
            return (headers ?? new()).Select(h => new KeyValuePair<string, string>(h.Key,
                Marker.Replace(h.Value ?? string.Empty, m => replacements.TryGetValue(m.Groups[1].Value, out var r) ? r : m.Value)));
        }

        private static async Task<string> ReadAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[Math.Max(1, maxBytes) + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct)) > 0)
                read += n;
            var truncated = read > maxBytes;
            var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, maxBytes));
            return truncated ? text + "\n… (response truncated)" : text;
        }
    }
}
