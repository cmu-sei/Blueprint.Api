// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ClientOptions = Blueprint.Api.Infrastructure.Options.ClientOptions;

namespace Blueprint.Api.Automation
{
    /// <summary>
    /// Where automation tasks may reach. A task runs as Blueprint's service account, inside Blueprint's network, so
    /// an author must not be able to point it anywhere: http requests go only to allowed hosts, over http or https,
    /// never to a link-local address (cloud metadata endpoints among them) or, unless allowed, a loopback one, and
    /// the service account's token goes only to the hosts named for it. SSH tasks reach only allowed hosts, and a
    /// file upload reads only from the configured directory.
    /// </summary>
    public interface IOutboundRequestGuard
    {
        /// <summary>The URL an http task may call; throws when it may not.</summary>
        Uri CheckHttpUrl(string url);
        /// <summary>Whether an http task to this address carries the service account's token.</summary>
        bool SendsServiceToken(Uri uri);
        /// <summary>Throws when an SSH task may not connect to the host.</summary>
        void CheckSshHost(string host);
        /// <summary>The full path of a file under the configured directory; throws for a path outside it.</summary>
        string ResolveLocalFile(string path);
        /// <summary>A client for http tasks: no redirects, and the address checks made as each connection opens.</summary>
        HttpClient CreateHttpClient();
    }

    public class OutboundRequestGuard : IOutboundRequestGuard
    {
        public const string HttpClientName = "automation-http";
        private readonly IOptionsMonitor<AutomationOptions> _options;
        private readonly IOptionsMonitor<ClientOptions> _clientOptions;
        private readonly IHttpClientFactory _httpClientFactory;

        public OutboundRequestGuard(IOptionsMonitor<AutomationOptions> options, IOptionsMonitor<ClientOptions> clientOptions, IHttpClientFactory httpClientFactory)
        {
            _options = options;
            _clientOptions = clientOptions;
            _httpClientFactory = httpClientFactory;
        }

        public Uri CheckHttpUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new AutomationConfigurationException($"'{url}' is not an absolute URL.");
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new AutomationConfigurationException($"An http task cannot use {uri.Scheme}; only http and https are allowed.");
            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new AutomationConfigurationException("An http task's URL cannot carry a user name or password.");
            var allowed = _options.CurrentValue.Http.AllowedHosts;
            if (!HostMatches(uri.Host, allowed is { Count: > 0 } ? allowed : CrucibleHosts()))
                throw new AutomationConfigurationException($"{uri.Host} is not one of the hosts http tasks may call (Automation:Http:AllowedHosts).");
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !IsAllowedAddress(literal, _options.CurrentValue.Http.AllowLoopbackAddresses))
                throw new AutomationConfigurationException($"An http task cannot call {uri.Host}.");
            return uri;
        }

        public bool SendsServiceToken(Uri uri)
        {
            // only to hosts named for it: with the token, a task could read any of an API's data as the service account
            var tokenHosts = _options.CurrentValue.Http.TokenHosts;
            return tokenHosts is { Count: > 0 } && HostMatches(uri.Host, tokenHosts) && !IsBlueprintApi(uri);
        }

        /// <summary>Whether the URL is Blueprint's own API, which a task must never call with Blueprint's service token.</summary>
        private bool IsBlueprintApi(Uri uri) =>
            Uri.TryCreate(_clientOptions.CurrentValue.BlueprintApiUrl, UriKind.Absolute, out var own)
            && string.Equals(own.Host, uri.Host, StringComparison.OrdinalIgnoreCase)
            && own.Port == uri.Port;

        public void CheckSshHost(string host)
        {
            var allowed = _options.CurrentValue.Ssh.AllowedHosts;
            if (allowed is not { Count: > 0 })
                throw new AutomationConfigurationException("SSH tasks are off: Automation:Ssh:AllowedHosts names no host.");
            if (!HostMatches(host, allowed))
                throw new AutomationConfigurationException($"{host} is not one of the hosts SSH tasks may connect to (Automation:Ssh:AllowedHosts).");
        }

        public string ResolveLocalFile(string path)
        {
            var directory = _options.CurrentValue.Files.LocalDirectory;
            if (string.IsNullOrWhiteSpace(directory))
                throw new AutomationConfigurationException("Server file uploads are off: Automation:Files:LocalDirectory is not set.");
            if (string.IsNullOrWhiteSpace(path))
                throw new AutomationConfigurationException("The server file is required.");
            var root = Path.GetFullPath(directory);
            var full = Path.GetFullPath(Path.Combine(root, path));
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                throw new AutomationConfigurationException($"{path} is not under the directory server files are uploaded from.");
            return full;
        }

        public HttpClient CreateHttpClient()
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.Http.TimeoutSeconds));
            return client;
        }

        /// <summary>The primary handler of <see cref="HttpClientName"/>: no redirects, and every address checked as it is connected to.</summary>
        public static SocketsHttpHandler CreatePrimaryHandler(IOptionsMonitor<AutomationOptions> options) => new()
        {
            AllowAutoRedirect = false,
            // a proxy would be connected to instead of the destination, and the address check would see only the proxy
            UseProxy = false,
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, options.CurrentValue.Http.AllowLoopbackAddresses, ct),
        };

        /// <summary>
        /// Resolves the host and connects to its first address the guard allows, so a name that resolves to a refused
        /// address (DNS rebinding included) is refused at connect time, whatever it resolved to when the URL was checked.
        /// </summary>
        public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, bool allowLoopback, CancellationToken ct)
        {
            var addresses = IPAddress.TryParse(endPoint.Host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(endPoint.Host, ct);
            var allowed = addresses.Where(a => IsAllowedAddress(a, allowLoopback)).ToList();
            if (allowed.Count == 0)
                throw new HttpRequestException($"An http task cannot connect to {endPoint.Host}: it resolves to no address tasks may reach.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed.ToArray(), endPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Whether a task may reach the address: never an unspecified, broadcast, multicast or link-local one (cloud
        /// metadata services answer on 169.254.169.254 and fd00:ec2::254), and a loopback one only when allowed.
        /// </summary>
        public static bool IsAllowedAddress(IPAddress address, bool allowLoopback)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address))
                return allowLoopback;
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
                return false;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) return false;   // link-local, metadata
                if (b[0] == 0) return false;                     // "this network"
                if (b[0] >= 224) return false;                   // multicast and reserved
                return true;
            }
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
                return false;
            if (address.Equals(IPAddress.Parse("fd00:ec2::254")))
                return false;
            return true;
        }

        /// <summary>Whether a host is one of the patterns: a name, or <c>*.domain</c> for the domain and every host in it.</summary>
        public static bool HostMatches(string host, IEnumerable<string> patterns)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            foreach (var raw in patterns ?? Enumerable.Empty<string>())
            {
                var pattern = raw?.Trim().TrimEnd('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(pattern))
                    continue;
                if (pattern.StartsWith("*."))
                {
                    var domain = pattern.Substring(2);
                    if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
                        return true;
                }
                else if (host == pattern)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The hosts of the Crucible APIs Blueprint is configured to call.</summary>
        private IReadOnlyList<string> CrucibleHosts()
        {
            var c = _clientOptions.CurrentValue;
            return new[] { c.CiteApiUrl, c.GalleryApiUrl, c.PlayerApiUrl, c.PlayerVmApiUrl, c.CasterApiUrl, c.BlueprintApiUrl }
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
                .Select(u => new Uri(u).Host)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
