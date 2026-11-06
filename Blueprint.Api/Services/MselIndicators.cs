// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// The scenario's indicator set: written once with the injects, made safe here, and shared by every document the build
    /// writes, so an alert, an intel report, an advisory and a CTI event name the same hosts, addresses, domains and hashes,
    /// and participants can pivot from one to the next.
    /// </summary>
    public static class MselIndicatorRules
    {
        public const string Ip = "ip";
        public const string Domain = "domain";
        public const string Sha256 = "sha256";
        public const string Email = "email";
        public const string UserAgent = "user-agent";
        public static readonly string[] Types = [Ip, Domain, Sha256, Email, UserAgent];
        public static readonly string[] Roles = ["Workstation", "Server", "DomainController", "Firewall", "Router", "WebServer", "Database", "MailServer", "Other"];

        /// <summary>The layout of an Intel article written as a MISP-style CTI event</summary>
        public const string CtiEvent = "CtiEvent";
        public const string StandardLayout = "Standard";
        /// <summary>A handful of CTI events a MSEL, not every Intel article</summary>
        public const int MaxCtiEvents = 5;
        private const int MaxAssets = 40;
        private const int MaxAdversary = 30;
        private const int MaxTechniques = 6;

        // RFC 5737 and RFC 3849: the addresses documentation may use
        private static readonly IPNetwork[] _documentation =
            [IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("198.51.100.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("2001:db8::/32")];
        // RFC 1918, and RFC 4193 unique local addresses
        private static readonly IPNetwork[] _private =
            [IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("fc00::/7")];
        private static readonly string[] _safeSuffixes = [".example", ".test", ".invalid", ".example.com", ".example.net", ".example.org"];
        private static readonly string[] _safeDomains = ["example.com", "example.net", "example.org"];
        private static readonly Regex _technique = new(@"^\s*(T\d{4}(?:\.\d{3})?)\b[\s:\-–—]*(.*)$", RegexOptions.IgnoreCase);
        private static readonly Regex _hostName = new(@"[^A-Za-z0-9._-]+");
        private static readonly Regex _label = new(@"[^a-z0-9-]+");

        public static bool IsDocumentation(IPAddress address) => _documentation.Any(n => n.Contains(address));
        public static bool IsPrivate(IPAddress address) => _private.Any(n => n.Contains(address));

        /// <summary>A reserved domain no one can register: .example, .test, .invalid, or under example.com, .net or .org</summary>
        public static bool IsSafeDomain(string domain)
        {
            domain = domain?.Trim().TrimEnd('.').ToLowerInvariant();
            return !string.IsNullOrEmpty(domain) && (_safeDomains.Contains(domain) || _safeSuffixes.Any(domain.EndsWith));
        }

        /// <summary>A network in CIDR form; an address inside it, such as 10.1.0.5/24, is taken as its network</summary>
        public static bool TryParseNetwork(string value, out IPNetwork network)
        {
            network = default;
            return !string.IsNullOrWhiteSpace(value) && value.Contains('/') && IPNetwork.TryParse(value.Trim(), out network);
        }

        /// <summary>
        /// The indicator set's schema, for the injects step: arrays of small objects with enums where they can be, since Bedrock's
        /// strict mode counts free-text fields
        /// </summary>
        public static JsonObject Schema(IReadOnlyCollection<string> organizations)
        {
            JsonObject Str(string description) => new JsonObject { ["type"] = "string", ["description"] = description };
            JsonObject Enum(IEnumerable<string> values, string description) =>
                new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()), ["description"] = description };
            JsonObject Arr(JsonObject properties, string description) => new JsonObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JsonObject { ["type"] = "object", ["properties"] = properties }
            };
            var properties = new JsonObject
            {
                ["threatActor"] = Str("The adversary's fictional name, as threat intelligence reports call it"),
                ["assets"] = Arr(new JsonObject
                {
                    ["hostName"] = Str("The host's name, e.g. dc01 or hr-ws-114"),
                    ["role"] = Enum(Roles, "What the host is"),
                    ["ipAddress"] = Str("Its address, from 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16"),
                    ["network"] = Str("The network it is on, in CIDR form, e.g. 10.20.1.0/24")
                }, "The defenders' hosts the documents name: the ones attacked, alerting or affected"),
                ["adversary"] = Arr(new JsonObject
                {
                    ["type"] = Enum(Types, "ip, domain, sha256 (a file), email (a sender address) or user-agent"),
                    ["value"] = Str("The value: an IP from 192.0.2.0/24, 198.51.100.0/24 or 203.0.113.0/24, a domain ending .example or .test, an address at one, a user agent; for a file, its name"),
                    ["purpose"] = Str("What it is for in a few words, e.g. C2 server, phishing sender, loader DLL")
                }, "The adversary's infrastructure: 5 to 15 indicators the documents share")
            };
            if (organizations.Count > 0)
                properties["domains"] = Arr(new JsonObject
                {
                    ["organization"] = Enum(organizations, "The organization's short name"),
                    ["domain"] = Str("Its own domain, ending .example, e.g. harborpower.example")
                }, "Each organization's own domain, for its email addresses and web site");
            return new JsonObject
            {
                ["type"] = "object",
                ["description"] = "The scenario's one set of indicators, which every document reuses",
                ["properties"] = properties
            };
        }

        /// <summary>
        /// Makes the model's indicator set safe and complete: the Caster workspace's machines as the hosts where there is one,
        /// private addresses for the hosts, documentation addresses and reserved domains for the adversary, hashes made up here,
        /// a domain for every organization, and at least one indicator of each kind the documents need. Every value replaced is
        /// counted in a warning.
        /// </summary>
        public static MselIndicatorSet Clean(MselIndicatorSet set, MselDraft draft, CasterRange range, List<string> warnings)
        {
            set ??= new MselIndicatorSet();
            var seed = draft.DraftId == Guid.Empty ? (draft.Name ?? "") : draft.DraftId.ToString();
            var replaced = new List<string>();
            var cleaned = new MselIndicatorSet
            {
                ThreatActor = string.IsNullOrWhiteSpace(set.ThreatActor) ? "an unattributed actor" : Shorten(set.ThreatActor, 80)
            };
            var external = (range?.ExternalNetworks ?? new List<string>())
                .Select(c => TryParseNetwork(c, out var n) ? n : (IPNetwork?)null).Where(n => n != null).Select(n => n.Value).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // the hosts: the range's machines, with the addresses Caster gives, else ones on their network
            var modelAssets = (set.Assets ?? new List<MselIndicatorAsset>()).Where(a => !string.IsNullOrWhiteSpace(a?.HostName)).ToList();
            var networks = new Dictionary<string, IPNetwork>(StringComparer.OrdinalIgnoreCase);
            IPNetwork InventedNetwork(string key)
            {
                key ??= "";
                if (!networks.TryGetValue(key, out var network))
                {
                    network = IPNetwork.Parse($"10.10.{10 + networks.Count}.0/24");
                    networks[key] = network;
                }
                return network;
            }
            if (range?.Hosts.Count > 0)
            {
                cleaned.AssetSource = "Caster";
                foreach (var host in range.Hosts.Take(MaxAssets))
                {
                    var asked = modelAssets.FirstOrDefault(a => a.HostName.Trim().Equals(host.Name, StringComparison.OrdinalIgnoreCase));
                    IPNetwork? network = TryParseNetwork(host.Network, out var n) ? n : null;
                    var address = host.IpAddresses.FirstOrDefault(a => !used.Contains(a))
                        ?? (IPAddress.TryParse(asked?.IpAddress?.Trim(), out var a) && IsPrivate(a) && (network == null || network.Value.Contains(a)) && !used.Contains(a.ToString()) ? a.ToString() : null)
                        ?? NextFree(network ?? InventedNetwork(host.NetworkName), used);
                    used.Add(address);
                    cleaned.Assets.Add(new MselIndicatorAsset
                    {
                        HostName = host.Name,
                        Role = Roles.FirstOrDefault(r => r.Equals(asked?.Role?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? RangeRole(host.Role),
                        IpAddress = address,
                        Network = (network ?? NetworkOf(address)).ToString(),
                        Gateway = host.Gateway
                    });
                }
                var invented = modelAssets.Where(a => !range.Hosts.Any(h => h.Name.Equals(a.HostName.Trim(), StringComparison.OrdinalIgnoreCase)))
                    .Select(a => a.HostName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (invented.Any())
                    warnings.Add($"Kept the Caster workspace's {range.Hosts.Count} machines as the scenario's hosts, and left out {invented.Count} the model made up: {string.Join(", ", invented)}.");
            }
            else
            {
                foreach (var asset in modelAssets.Take(MaxAssets))
                {
                    var name = _hostName.Replace(asset.HostName.Trim(), "-").Trim('-', '.');
                    if (name.Length == 0 || cleaned.Assets.Any(x => x.HostName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    IPNetwork? network = TryParseNetwork(asset.Network, out var n) && IsPrivate(n.BaseAddress) ? n : null;
                    string address;
                    if (IPAddress.TryParse(asset.IpAddress?.Trim(), out var a) && IsPrivate(a) && !used.Contains(a.ToString()) && (network == null || network.Value.Contains(a)))
                        address = a.ToString();
                    else
                    {
                        address = NextFree(network ?? InventedNetwork(asset.Network), used);
                        if (!string.IsNullOrWhiteSpace(asset.IpAddress))
                            replaced.Add($"{asset.IpAddress.Trim()} with {address}");
                    }
                    used.Add(address);
                    cleaned.Assets.Add(new MselIndicatorAsset
                    {
                        HostName = name,
                        Role = Roles.FirstOrDefault(r => r.Equals(asset.Role?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Other",
                        IpAddress = address,
                        Network = (network ?? NetworkOf(address)).ToString()
                    });
                }
            }

            // each organization's own domain, a reserved one
            var organizations = draft.Organizations ?? new List<MselDraftOrganization>();
            foreach (var organization in organizations.Where(o => !string.IsNullOrWhiteSpace(o.ShortName)))
            {
                var asked = (set.Domains ?? new List<MselIndicatorDomain>()).FirstOrDefault(d => organization.ShortName.Equals(d?.Organization?.Trim(), StringComparison.OrdinalIgnoreCase))?.Domain?.Trim().TrimEnd('.').ToLowerInvariant();
                var domain = IsSafeDomain(asked) ? asked : SafeDomain(string.IsNullOrWhiteSpace(asked) ? organization.ShortName : asked);
                if (!string.IsNullOrWhiteSpace(asked) && asked != domain)
                    replaced.Add($"{asked} with {domain}");
                domain = Unique(domain, cleaned.Domains.Select(d => d.Domain));
                cleaned.Domains.Add(new MselIndicatorDomain { Organization = organization.ShortName, Domain = domain });
            }

            // the adversary's infrastructure, in safe ranges; the hashes are made up here, never a real file's
            var adversaryAddresses = external.Concat(_documentation.Take(3)).ToList();
            var index = 0;
            foreach (var indicator in (set.Adversary ?? new List<MselIndicator>()).Where(i => i != null).Take(MaxAdversary))
            {
                var type = Types.FirstOrDefault(t => t.Equals(indicator.Type?.Trim(), StringComparison.OrdinalIgnoreCase));
                var value = indicator.Value?.Trim();
                if (type == null || (string.IsNullOrEmpty(value) && type != Sha256))
                    continue;
                var purpose = string.IsNullOrWhiteSpace(indicator.Purpose) ? null : Shorten(indicator.Purpose, 120);
                var safe = type switch
                {
                    Ip => IPAddress.TryParse(value, out var a) && (IsDocumentation(a) || external.Any(n => n.Contains(a))) && !used.Contains(a.ToString())
                        ? a.ToString() : NextFree(adversaryAddresses, used),
                    Domain => IsSafeDomain(value) ? value.TrimEnd('.').ToLowerInvariant() : SafeDomain(value),
                    Email => SafeEmail(value),
                    UserAgent => Shorten(value.Replace("<", "").Replace(">", ""), 200),
                    _ => Hex(SHA256.HashData, seed, index)
                };
                if (type == Domain)
                    safe = Unique(safe, cleaned.Domains.Select(d => d.Domain).Concat(cleaned.Adversary.Where(a => a.Type == Domain).Select(a => a.Value)));
                if (type != Sha256 && type != UserAgent && !safe.Equals(value, StringComparison.OrdinalIgnoreCase))
                    replaced.Add($"{value} with {safe}");
                if (type == Ip)
                    used.Add(safe);
                if (type != Sha256 && cleaned.Adversary.Any(x => x.Type == type && x.Value.Equals(safe, StringComparison.OrdinalIgnoreCase)))
                    continue;
                cleaned.Adversary.Add(new MselIndicator
                {
                    Type = type,
                    Value = safe,
                    Purpose = purpose,
                    // a file's name, which the model gave as its value
                    Name = type == Sha256 && !string.IsNullOrEmpty(value) && !Regex.IsMatch(value, "^[0-9a-fA-F]{32,64}$")
                        ? Shorten(Regex.Replace(value, @"[<>""]", ""), 80) : null,
                    Md5 = type == Sha256 ? Hex(MD5.HashData, seed, index) : null,
                    Sha1 = type == Sha256 ? Hex(SHA1.HashData, seed, index) : null
                });
                index++;
            }
            // the range's attacker machines are adversary addresses too
            foreach (var attacker in range?.Attackers ?? new List<CasterRangeHost>())
                foreach (var address in attacker.IpAddresses.Where(a => !used.Contains(a)))
                {
                    used.Add(address);
                    cleaned.Adversary.Add(new MselIndicator { Type = Ip, Value = address, Purpose = $"Adversary host {attacker.Name}" });
                }

            // at least one of each kind the documents name, so a stray value always has one to become
            var adversaryDomain = cleaned.Adversary.FirstOrDefault(a => a.Type == Domain)?.Value;
            if (adversaryDomain == null)
            {
                adversaryDomain = Unique("update-check.example", cleaned.Domains.Select(d => d.Domain));
                cleaned.Adversary.Add(new MselIndicator { Type = Domain, Value = adversaryDomain, Purpose = "C2 domain" });
            }
            if (!cleaned.Adversary.Any(a => a.Type == Ip))
            {
                var address = NextFree(adversaryAddresses, used);
                used.Add(address);
                cleaned.Adversary.Add(new MselIndicator { Type = Ip, Value = address, Purpose = "C2 server" });
            }
            if (!cleaned.Adversary.Any(a => a.Type == Sha256))
                cleaned.Adversary.Add(new MselIndicator { Type = Sha256, Value = Hex(SHA256.HashData, seed, index), Md5 = Hex(MD5.HashData, seed, index), Sha1 = Hex(SHA1.HashData, seed, index), Purpose = "Malicious attachment" });
            if (!cleaned.Adversary.Any(a => a.Type == Email))
                cleaned.Adversary.Add(new MselIndicator { Type = Email, Value = "notice@" + adversaryDomain, Purpose = "Phishing sender" });

            if (replaced.Any())
                warnings.Add($"Replaced {replaced.Count} {(replaced.Count == 1 ? "indicator" : "indicators")} outside the safe ranges (RFC 5737 and RFC 1918 addresses, .example and .test domains): {string.Join("; ", replaced.Take(10))}{(replaced.Count > 10 ? "; ..." : "")}.");
            return cleaned;
        }

        /// <summary>
        /// Each inject's share of the set: only indicator values the set has, its techniques as ID and name, the CTI event layout
        /// on a handful of Intel articles, and its IP Address and Network columns from the set
        /// </summary>
        public static void CleanInjects(MselDraft draft, MselEntity template, List<string> warnings)
        {
            var set = draft.Indicators;
            if (set == null)
                return;
            var lookup = Lookup(set);
            var dropped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceType = template.DataFields.FirstOrDefault(f => f.GalleryArticleParameter == nameof(GalleryArticleParameter.SourceType));
            var ipFields = MselDraftRules.DraftedFields(template).Where(f => f.DataType == DataFieldType.IpAddress).ToList();
            var networkFields = MselDraftRules.DraftedFields(template).Where(f => f.DataType == DataFieldType.Network).ToList();
            var addresses = set.Assets.Select(a => a.IpAddress).Concat(set.Adversary.Where(a => a.Type == Ip).Select(a => a.Value)).ToList();
            var setNetworks = SetNetworks(set);
            var ctiEvents = 0;
            var correctedColumns = 0;
            foreach (var inject in draft.Injects)
            {
                var indicators = new List<string>();
                foreach (var value in (inject.Indicators ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()))
                {
                    if (!lookup.TryGetValue(value, out var known))
                        dropped.Add(value);
                    else if (!indicators.Contains(known, StringComparer.OrdinalIgnoreCase))
                        indicators.Add(known);
                }
                inject.Indicators = indicators;
                inject.Techniques = (inject.Techniques ?? new List<string>())
                    .Select(t => _technique.Match(t ?? ""))
                    .Where(m => m.Success)
                    .Select(m => (m.Groups[1].Value.ToUpperInvariant() + " " + Shorten(m.Groups[2].Value, 80)).Trim())
                    .DistinctBy(t => t.Split(' ')[0]).Take(MaxTechniques).ToList();
                var isIntel = sourceType != null && inject.Values != null && inject.Values.TryGetValue(sourceType.Name, out var type)
                    && type.Equals(nameof(GallerySourceType.Intel), StringComparison.OrdinalIgnoreCase);
                inject.Layout = isIntel && CtiEvent.Equals(inject.Layout?.Trim(), StringComparison.OrdinalIgnoreCase) && ctiEvents++ < MaxCtiEvents ? CtiEvent : null;

                // the row's address, and the network it is on, from the set
                var rowAddress = inject.Indicators.Select(v => set.Assets.FirstOrDefault(a => a.HostName.Equals(v, StringComparison.OrdinalIgnoreCase))?.IpAddress ?? v)
                    .FirstOrDefault(v => addresses.Contains(v));
                foreach (var field in ipFields)
                {
                    var value = inject.Values.TryGetValue(field.Name, out var v) ? v?.Trim() : null;
                    var known = IPAddress.TryParse(value, out var a) ? addresses.FirstOrDefault(x => x == a.ToString()) : null;
                    if (known == null && !string.IsNullOrEmpty(value))
                        correctedColumns++;
                    known ??= rowAddress;
                    if (known == null)
                        inject.Values.Remove(field.Name);
                    else
                        inject.Values[field.Name] = known;
                    // the network column follows the address the row shows
                    rowAddress = known ?? rowAddress;
                }
                foreach (var field in networkFields)
                {
                    var value = inject.Values.TryGetValue(field.Name, out var v) ? v?.Trim() : null;
                    var known = TryParseNetwork(value, out var n) ? setNetworks.FirstOrDefault(x => x == n.ToString()) : null;
                    if (known == null && !string.IsNullOrEmpty(value))
                        correctedColumns++;
                    known ??= rowAddress != null && IPAddress.TryParse(rowAddress, out var a)
                        ? set.Assets.FirstOrDefault(x => x.IpAddress == rowAddress)?.Network ?? setNetworks.FirstOrDefault(x => IPNetwork.Parse(x).Contains(a))
                        : null;
                    if (known == null)
                        inject.Values.Remove(field.Name);
                    else
                        inject.Values[field.Name] = known;
                }
            }
            if (dropped.Any())
                warnings.Add($"Took {dropped.Count} {(dropped.Count == 1 ? "value" : "values")} off injects that the scenario's indicator set does not have: {string.Join(", ", dropped.Take(10))}{(dropped.Count > 10 ? ", ..." : "")}.");
            if (ctiEvents > MaxCtiEvents)
                warnings.Add($"Kept the CTI event layout on the first {MaxCtiEvents} of {ctiEvents} Intel articles; the others are intelligence reports.");
            if (correctedColumns > 0)
                warnings.Add($"Set {correctedColumns} IP Address or Network {(correctedColumns == 1 ? "value" : "values")} that the scenario's indicator set does not have from the set.");
        }

        /// <summary>The set's values, case-insensitively, as the set writes them: host names, addresses, domains, hashes, senders</summary>
        public static Dictionary<string, string> Lookup(MselIndicatorSet set)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in set.Assets)
            {
                lookup.TryAdd(asset.HostName, asset.HostName);
                lookup.TryAdd(asset.IpAddress, asset.IpAddress);
            }
            foreach (var domain in set.Domains)
                lookup.TryAdd(domain.Domain, domain.Domain);
            foreach (var indicator in set.Adversary)
            {
                foreach (var value in new[] { indicator.Value, indicator.Md5, indicator.Sha1 }.Where(v => !string.IsNullOrEmpty(v)))
                    lookup.TryAdd(value, value);
                // a file named by its name is the file
                if (!string.IsNullOrEmpty(indicator.Name))
                    lookup.TryAdd(indicator.Name, indicator.Value);
            }
            return lookup;
        }

        /// <summary>The networks the set's hosts are on</summary>
        public static List<string> SetNetworks(MselIndicatorSet set) =>
            set.Assets.Select(a => a.Network).Where(n => TryParseNetwork(n, out _)).Distinct().ToList();

        /// <summary>
        /// What a document's writer is told of the set: the actor, the organizations' domains, and the indicators it names, or
        /// for an intelligence product with none named, the adversary's
        /// </summary>
        public static string Describe(MselIndicatorSet set, MselDraftInject inject, bool intelligence)
        {
            if (set == null)
                return null;
            var named = new HashSet<string>(inject?.Indicators ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var assets = set.Assets.Where(a => named.Contains(a.HostName) || named.Contains(a.IpAddress)).ToList();
            var adversary = set.Adversary.Where(a => named.Contains(a.Value) || (a.Md5 != null && (named.Contains(a.Md5) || named.Contains(a.Sha1)))).ToList();
            if (!assets.Any() && !adversary.Any() && intelligence)
                adversary = set.Adversary;
            var sb = new StringBuilder();
            sb.AppendLine("The scenario's indicators. Name only these values: no other IP address, network, domain, file hash or email address.");
            sb.AppendLine("Blueprint replaces any value the scenario's indicator set does not have. Organizations' people write from their own domain.");
            sb.AppendLine($"Threat actor: {set.ThreatActor}");
            if (set.Domains.Any())
                sb.AppendLine($"Organization domains: {string.Join("; ", set.Domains.Select(d => $"{d.Organization} {d.Domain}"))}");
            foreach (var asset in assets)
                sb.AppendLine($"- Host {asset.HostName} ({asset.Role}): {asset.IpAddress} on {asset.Network}{(asset.Gateway == null ? "" : $", gateway {asset.Gateway}")}");
            foreach (var indicator in adversary)
                sb.AppendLine(indicator.Type == Sha256
                    ? $"- File {indicator.Name ?? ""}{(indicator.Name == null ? "" : " ")}({indicator.Purpose ?? "malicious file"}): SHA-256 {indicator.Value}, MD5 {indicator.Md5}, SHA-1 {indicator.Sha1}"
                    : $"- {TypeName(indicator.Type)} ({indicator.Purpose ?? "adversary"}): {indicator.Value}");
            if (!assets.Any() && !adversary.Any())
                sb.AppendLine("This document names none of the indicators.");
            return sb.ToString();
        }

        /// <summary>
        /// The CTI event an Intel article lays out: its header values, its tags (the TLP marking, the threat actor, the ATT&amp;CK
        /// techniques of the inject and of the injects that share its indicators), the attribute rows, and the related events
        /// </summary>
        public static string CtiEventRequest(MselDraft draft, int injectIndex, string datePosted, Func<MselDraftInject, string> title, Func<MselDraftInject, string> date)
        {
            var set = draft.Indicators ?? new MselIndicatorSet();
            var inject = draft.Injects[injectIndex];
            var named = new HashSet<string>(inject.Indicators ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            bool Shares(MselDraftInject other) => (other.Indicators ?? new List<string>()).Any(named.Contains);
            var related = draft.Injects.Select((i, n) => (Inject: i, Index: n))
                .Where(x => x.Index != injectIndex && Shares(x.Inject) && !string.IsNullOrWhiteSpace(title(x.Inject)))
                .Take(6).ToList();
            var techniques = (inject.Techniques ?? new List<string>()).Concat(related.SelectMany(r => r.Inject.Techniques ?? new List<string>()))
                .DistinctBy(t => t.Split(' ')[0]).Take(8).ToList();
            var (marking, level) = ArticleMarkings.Clean(draft.Design?.Marking, draft.Design?.MarkingText, ArticleMarkings.Exercise);
            var tags = new List<string>();
            if (marking == ArticleMarkings.Tlp)
                tags.Add("tlp:" + level.ToLowerInvariant());
            tags.Add($"misp-galaxy:threat-actor=\"{set.ThreatActor}\"");
            tags.AddRange(techniques.Select(t =>
            {
                var parts = t.Split(' ', 2);
                return $"misp-galaxy:mitre-attack-pattern=\"{(parts.Length > 1 ? parts[1] + " - " : "")}{parts[0]}\"";
            }));

            var sb = new StringBuilder();
            sb.AppendLine("Lay this Intel article out as a CTI event (MISP style), with these values:");
            sb.AppendLine($"Event ID: {EventId(injectIndex)}");
            sb.AppendLine($"Date: {datePosted}");
            sb.AppendLine($"Tags: {string.Join("; ", tags)}");
            sb.AppendLine("Attributes (category | type | value | IDS | comment); one row each, in this order:");
            foreach (var asset in set.Assets.Where(a => named.Contains(a.HostName) || named.Contains(a.IpAddress)))
                sb.AppendLine($"- Network activity | ip-dst | {asset.IpAddress} | No | Affected host {asset.HostName} ({asset.Role})");
            foreach (var indicator in set.Adversary.Where(a => named.Contains(a.Value) || (a.Md5 != null && (named.Contains(a.Md5) || named.Contains(a.Sha1)))))
            {
                var comment = indicator.Purpose ?? indicator.Name ?? "";
                switch (indicator.Type)
                {
                    case Ip: sb.AppendLine($"- Network activity | ip-dst | {indicator.Value} | Yes | {comment}"); break;
                    case Domain: sb.AppendLine($"- Network activity | domain | {indicator.Value} | Yes | {comment}"); break;
                    case Email: sb.AppendLine($"- Payload delivery | email-src | {indicator.Value} | Yes | {comment}"); break;
                    case UserAgent: sb.AppendLine($"- Network activity | user-agent | {indicator.Value} | No | {comment}"); break;
                    default:
                        if (indicator.Name != null)
                            sb.AppendLine($"- Payload delivery | filename | {indicator.Name} | No | {indicator.Purpose ?? ""}");
                        sb.AppendLine($"- Payload delivery | sha256 | {indicator.Value} | Yes | {comment}");
                        sb.AppendLine($"- Payload delivery | md5 | {indicator.Md5} | Yes | {comment}");
                        sb.AppendLine($"- Payload delivery | sha1 | {indicator.Sha1} | No | {comment}");
                        break;
                }
            }
            sb.AppendLine(related.Any() ? "Related events (sharing its indicators):" : "Related events: none yet.");
            foreach (var (other, n) in related)
                sb.AppendLine($"- Event {EventId(n)}, {date(other)}: {title(other)}");
            return sb.ToString();
        }

        /// <summary>A CTI event's number, the same in its own article and in the events that relate to it</summary>
        public static int EventId(int injectIndex) => 1001 + injectIndex;

        private static string TypeName(string type) => type switch
        {
            Ip => "IP address",
            Domain => "Domain",
            Email => "Sender address",
            UserAgent => "User agent",
            _ => type
        };

        private static string RangeRole(string role) => role switch
        {
            "Linux" or "Windows" or null => "Server",
            _ => Roles.Contains(role) ? role : "Other"
        };

        private static IPNetwork NetworkOf(string address)
        {
            var a = IPAddress.Parse(address);
            return a.AddressFamily == AddressFamily.InterNetwork ? new IPNetwork(Mask(a, 24), 24) : new IPNetwork(Mask(a, 64), 64);
        }

        private static IPAddress Mask(IPAddress address, int prefix)
        {
            var bytes = address.GetAddressBytes();
            for (var i = 0; i < bytes.Length; i++)
            {
                var bits = Math.Clamp(prefix - i * 8, 0, 8);
                bytes[i] &= (byte)(0xFF << (8 - bits));
            }
            return new IPAddress(bytes);
        }

        private static string NextFree(IPNetwork network, HashSet<string> used) => NextFree([network], used);

        // the first address from .10 up in the first network with one free
        private static string NextFree(IEnumerable<IPNetwork> networks, HashSet<string> used)
        {
            foreach (var network in networks)
            {
                var size = network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? Math.Min(1L << (32 - network.PrefixLength), 1L << 16) : 1L << 16;
                for (long offset = 10; offset < size - 1; offset++)
                {
                    var bytes = network.BaseAddress.GetAddressBytes();
                    var carry = offset;
                    for (var i = bytes.Length - 1; i >= 0 && carry > 0; i--)
                    {
                        var sum = bytes[i] + (carry & 0xFF);
                        bytes[i] = (byte)sum;
                        carry = (carry >> 8) + (sum >> 8);
                    }
                    var address = new IPAddress(bytes).ToString();
                    if (!used.Contains(address))
                        return address;
                }
            }
            return "203.0.113.254";
        }

        /// <summary>A reserved domain kept as close to the one given as it can be: its first label under .example</summary>
        public static string SafeDomain(string domain)
        {
            var first = (domain ?? "").Trim().ToLowerInvariant().Split('@').Last().Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            first = _label.Replace(first, "-").Trim('-');
            return (first.Length == 0 ? "adversary" : first.Length > 40 ? first[..40] : first) + ".example";
        }

        private static string SafeEmail(string value)
        {
            var at = value.LastIndexOf('@');
            var local = at > 0 ? value[..at] : "notice";
            local = Regex.Replace(local.ToLowerInvariant(), @"[^a-z0-9._+-]+", ".").Trim('.');
            var domain = at > 0 ? value[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant() : value;
            return (local.Length == 0 ? "notice" : local) + "@" + (IsSafeDomain(domain) ? domain : SafeDomain(domain));
        }

        private static string Unique(string domain, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(domain))
                return domain;
            var dot = domain.IndexOf('.');
            for (var n = 2; ; n++)
                if (!set.Contains($"{domain[..dot]}{n}{domain[dot..]}"))
                    return $"{domain[..dot]}{n}{domain[dot..]}";
        }

        // random-looking hex of a hash's length, the same for the same draft each time
        private static string Hex(Func<byte[], byte[]> hash, string seed, int index) =>
            Convert.ToHexStringLower(hash(Encoding.UTF8.GetBytes($"blueprint-indicator:{seed}:{index}")));

        private static string Shorten(string value, int length)
        {
            value = Regex.Replace(value.Trim(), @"\s+", " ");
            return value.Length <= length ? value : value[..length];
        }
    }
}
