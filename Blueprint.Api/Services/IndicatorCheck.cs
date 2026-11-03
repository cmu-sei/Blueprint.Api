// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// The check after each document is written: every IP address, network, domain, file hash and email address in it that the
    /// scenario's indicator set does not have is replaced with one the set has, of the same kind, the same one each time for
    /// the same value. It is kept conservative: data: URIs are skipped, and version numbers, times, phone numbers, file names and
    /// words with a dot in them are not addresses or domains.
    /// </summary>
    public static class IndicatorCheck
    {
        private const string Dot = @"(?:\.|\[\.\])";
        private static readonly Regex _dataUri = new(@"data:[^""'\s>)]*", RegexOptions.IgnoreCase);
        private static readonly Regex _email = new(@"(?<![\w.%+-])[A-Za-z0-9._%+-]+@(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?" + Dot + @")+[A-Za-z]{2,24}(?![\w-])");
        private static readonly Regex _ipv4 = new(@"(?<![\w.\[\]-])(\d{1,3})" + Dot + @"(\d{1,3})" + Dot + @"(\d{1,3})" + Dot + @"(\d{1,3})(?:/(\d{1,2}))?(?![\w-]|\.\d|\[\.\]\d)");
        private static readonly Regex _ipv6 = new(@"(?<![\w:.])(?:[0-9A-Fa-f]{1,4})?(?::[0-9A-Fa-f]{0,4}){2,7}(?:/(\d{1,3}))?(?![\w:])");
        private static readonly Regex _hash = new(@"(?<![0-9A-Za-z])(?:[0-9A-Fa-f]{64}|[0-9A-Fa-f]{40}|[0-9A-Fa-f]{32})(?![0-9A-Za-z])");
        private static readonly Regex _domain = new(@"(?<![\w@.-])(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?" + Dot + @")+([A-Za-z]{2,24})(?![\w-]|\.\w|\[\.\]\w)");
        // a number that follows one of these is a version, build, section or step, not an address
        private static readonly Regex _notAnAddress = new(@"\b(?:version|ver\.?|build|release|rev\.?|section|para\.?|paragraph|step)\s*$|§\s*$", RegexOptions.IgnoreCase);

        // the top-level domains a real domain in a document ends with; others (exe, pdf, js, md, sh, zip ...) are file names
        private static readonly HashSet<string> _realTlds = new(StringComparer.OrdinalIgnoreCase)
        {
            "com", "net", "org", "info", "biz", "io", "co", "us", "uk", "ru", "cn", "su", "ir", "kp", "de", "fr", "nl", "eu", "br", "in", "jp",
            "kr", "tw", "hk", "sg", "au", "ca", "xyz", "top", "online", "site", "club", "live", "app", "dev", "cloud", "tech", "store", "shop",
            "link", "click", "pw", "cc", "tk", "ml", "ga", "cf", "gq", "me", "tv", "ws", "mobi", "gov", "mil", "edu", "int", "onion", "ua", "by",
            "kz", "tr", "vn", "th", "my", "ph", "pk", "ng", "za", "mx", "ar", "cl", "it", "es", "pt", "ch", "at", "be", "se", "no", "dk", "fi",
            "cz", "ro", "hu", "gr", "il", "ae", "sa", "qa", "eg", "nz", "ie", "news", "email", "support", "services", "network", "systems"
        };
        // reserved for documentation and tests: a domain under one of these is checked against the set too
        private static readonly HashSet<string> _reservedTlds = new(StringComparer.OrdinalIgnoreCase) { "example", "test", "invalid" };
        // the SVG namespace and the like, which are not the scenario's
        private static readonly string[] _allowed = ["w3.org", "www.w3.org", "xmlsoap.org", "openxmlformats.org"];

        public record Replacement(string Kind, string From, string To);

        /// <summary>
        /// The document with every value the set does not have replaced, and each replacement in replaced. knownEmails are the
        /// MSEL's own organizations' and teams' addresses, which stand, as do the domains they are at.
        /// </summary>
        public static string Enforce(string html, MselIndicatorSet set, IEnumerable<string> knownEmails, List<Replacement> replaced)
        {
            if (string.IsNullOrEmpty(html) || set == null)
                return html;
            var known = new Known(set, knownEmails);
            html = Pass(html, _email, null, m => known.Email(m.Value), replaced);
            html = Pass(html, _ipv4, null, m => known.Ipv4(m, html), replaced);
            html = Pass(html, _ipv6, null, m => known.Ipv6(m.Value), replaced);
            html = Pass(html, _hash, null, m => known.Hash(m.Value), replaced);
            html = Pass(html, _domain, _email, m => known.Domain(m.Value, m.Groups[1].Value), replaced);
            return html;
        }

        /// <summary>The warning a document's replacements give the planner; null for none</summary>
        public static string Warning(string document, List<Replacement> replaced)
        {
            if (replaced.Count == 0)
                return null;
            var listed = replaced.GroupBy(r => (r.From, r.To)).Select(g => $"{g.Key.From} with {g.Key.To}").ToList();
            return $"Replaced {replaced.Count} {(replaced.Count == 1 ? "value" : "values")} not in the scenario's indicator set in {document}: " +
                $"{string.Join("; ", listed.Take(8))}{(listed.Count > 8 ? "; ..." : "")}.";
        }

        // replaces each match outside a data: URI (and outside a match of skip) that check answers a replacement for
        private static string Pass(string html, Regex pattern, Regex skip, Func<Match, (string Kind, string To)?> check, List<Replacement> replaced)
        {
            var skipped = _dataUri.Matches(html).Cast<Match>().Concat(skip?.Matches(html).Cast<Match>() ?? Enumerable.Empty<Match>())
                .Select(m => (m.Index, End: m.Index + m.Length)).ToList();
            return pattern.Replace(html, m =>
            {
                if (skipped.Any(s => m.Index < s.End && m.Index + m.Length > s.Index))
                    return m.Value;
                var result = check(m);
                if (result is not (string kind, string to) || to == null)
                    return m.Value;
                // a value written defanged, as analysts do, stays defanged
                if (m.Value.Contains("[.]"))
                    to = to.Replace(".", "[.]");
                replaced.Add(new Replacement(kind, m.Value, to));
                return to;
            });
        }

        private class Known
        {
            private readonly HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<IPNetwork> _networks = new();
            private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _emails = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _emailDomains = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _hashes = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _internalPool;
            private readonly List<string> _adversaryPool;
            private readonly List<string> _networkPool;
            private readonly List<string> _domainPool;
            private readonly List<string> _emailPool;
            private readonly Dictionary<int, List<string>> _hashPool;

            public Known(MselIndicatorSet set, IEnumerable<string> knownEmails)
            {
                foreach (var asset in set.Assets)
                {
                    _addresses.Add(asset.IpAddress);
                    if (asset.Gateway != null)
                        _addresses.Add(asset.Gateway);
                }
                foreach (var indicator in set.Adversary.Where(i => i.Type == MselIndicatorRules.Ip))
                    _addresses.Add(indicator.Value);
                foreach (var network in MselIndicatorRules.SetNetworks(set))
                    _networks.Add(IPNetwork.Parse(network));
                foreach (var domain in set.Domains)
                {
                    _domains.Add(domain.Domain);
                    _emailDomains.Add(domain.Domain);
                }
                foreach (var indicator in set.Adversary)
                {
                    switch (indicator.Type)
                    {
                        case MselIndicatorRules.Domain:
                            _domains.Add(indicator.Value);
                            break;
                        case MselIndicatorRules.Email:
                            _emails.Add(indicator.Value);
                            _domains.Add(indicator.Value.Split('@').Last());
                            break;
                        case MselIndicatorRules.Sha256:
                            foreach (var hash in new[] { indicator.Value, indicator.Md5, indicator.Sha1 }.Where(h => h != null))
                                _hashes.Add(hash);
                            break;
                    }
                }
                foreach (var email in (knownEmails ?? Enumerable.Empty<string>()).Where(e => e != null && e.Contains('@')))
                {
                    _emails.Add(email.Trim());
                    _domains.Add(email.Trim().Split('@').Last());
                    _emailDomains.Add(email.Trim().Split('@').Last());
                }
                _internalPool = set.Assets.Select(a => a.IpAddress).ToList();
                _adversaryPool = set.Adversary.Where(i => i.Type == MselIndicatorRules.Ip).Select(i => i.Value).ToList();
                _networkPool = _networks.Select(n => n.ToString()).ToList();
                _domainPool = set.Adversary.Where(i => i.Type == MselIndicatorRules.Domain).Select(i => i.Value).ToList();
                _emailPool = set.Adversary.Where(i => i.Type == MselIndicatorRules.Email).Select(i => i.Value).ToList();
                _hashPool = new Dictionary<int, List<string>>
                {
                    [64] = set.Adversary.Where(i => i.Type == MselIndicatorRules.Sha256).Select(i => i.Value).ToList(),
                    [40] = set.Adversary.Where(i => i.Sha1 != null).Select(i => i.Sha1).ToList(),
                    [32] = set.Adversary.Where(i => i.Md5 != null).Select(i => i.Md5).ToList()
                };
            }

            public (string, string)? Email(string value)
            {
                var plain = value.Replace("[.]", ".");
                var domain = plain.Split('@').Last();
                // the set's senders, the MSEL's own addresses, and anyone at an organization's own domain
                if (_emails.Contains(plain) || _emailDomains.Any(d => domain.Equals(d, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
                    || !IsDomainToCheck(domain))
                    return null;
                return ("email", Pick(_emailPool, plain));
            }

            public (string, string)? Ipv4(Match m, string html)
            {
                var octets = Enumerable.Range(1, 4).Select(i => m.Groups[i].Value).ToList();
                // leading zeros, or an octet over 255, is no address
                if (octets.Any(o => (o.Length > 1 && o[0] == '0') || int.Parse(o) > 255))
                    return null;
                var before = html[..m.Index];
                if (_notAnAddress.IsMatch(before.Length > 12 ? before[^12..] : before))
                    return null;
                var address = IPAddress.Parse(string.Join(".", octets));
                if (m.Groups[5].Success)
                {
                    var prefix = int.Parse(m.Groups[5].Value);
                    if (prefix > 32)
                        return null;
                    var network = new IPNetwork(MaskedAddress(address, prefix), prefix);
                    // a network the set's hosts are on, or one holding a set address, stands
                    if (_networks.Contains(network) || _addresses.Any(a => IPAddress.TryParse(a, out var x) && network.Contains(x)) || IsHarmless(address))
                        return null;
                    var pool = MselIndicatorRules.IsPrivate(address) && _networkPool.Any() ? _networkPool : null;
                    return pool == null ? null : ("network", Pick(pool, network.ToString()));
                }
                if (_addresses.Contains(address.ToString()) || IsHarmless(address) || _networks.Any(n => n.BaseAddress.Equals(address)))
                    return null;
                return ("ip", Pick(MselIndicatorRules.IsPrivate(address) && _internalPool.Any() ? _internalPool : _adversaryPool, address.ToString()));
            }

            public (string, string)? Ipv6(string value)
            {
                var text = value.Split('/')[0];
                // a time (10:30:00) or a MAC address is no IPv6 address: one has :: or all eight groups
                if (!(text.Contains("::") || text.Count(c => c == ':') == 7) || !IPAddress.TryParse(text, out var address)
                    || address.AddressFamily != AddressFamily.InterNetworkV6 || text.Length < 3)
                    return null;
                if (_addresses.Contains(address.ToString()) || address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal)
                    return null;
                if (value.Contains('/'))
                    return null;
                return ("ip", Pick(MselIndicatorRules.IsPrivate(address) && _internalPool.Any() ? _internalPool : _adversaryPool, address.ToString()));
            }

            public (string, string)? Hash(string value)
            {
                // a long number or a word is no hash: one has both digits and letters
                if (!value.Any(char.IsDigit) || !value.Any(char.IsLetter) || _hashes.Contains(value))
                    return null;
                return _hashPool.TryGetValue(value.Length, out var pool) && pool.Any() ? ("hash", Pick(pool, value.ToLowerInvariant())) : null;
            }

            public (string, string)? Domain(string value, string tld)
            {
                var plain = value.Replace("[.]", ".").ToLowerInvariant();
                // ASP.NET and the like, written in capitals, are names, not domains
                if (!value.Any(char.IsLower) || !IsDomainToCheck(plain))
                    return null;
                if (_allowed.Concat(_domains).Any(d => plain == d.ToLowerInvariant() || plain.EndsWith("." + d.ToLowerInvariant())))
                    return null;
                return ("domain", Pick(_domainPool, plain));
            }

            // a domain worth checking: a real top-level domain, or one reserved for examples; .local, .corp and file names are not
            private static bool IsDomainToCheck(string domain)
            {
                var tld = domain.Split('.').Last();
                return _realTlds.Contains(tld) || _reservedTlds.Contains(tld) || MselIndicatorRules.IsSafeDomain(domain);
            }

            // loopback, any, broadcast and netmasks
            private static bool IsHarmless(IPAddress address)
            {
                if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
                    return true;
                var bytes = address.GetAddressBytes();
                var bits = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
                // a netmask is ones then zeros
                return bytes[0] == 255 && (~bits & (~bits + 1)) == 0;
            }

            private static IPAddress MaskedAddress(IPAddress address, int prefix)
            {
                var bytes = address.GetAddressBytes();
                for (var i = 0; i < bytes.Length; i++)
                    bytes[i] &= (byte)(0xFF << (8 - Math.Clamp(prefix - i * 8, 0, 8)));
                return new IPAddress(bytes);
            }

            // the same stray value becomes the same set value, in every document
            private static string Pick(List<string> pool, string value)
            {
                if (pool == null || pool.Count == 0)
                    return null;
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
                return pool[(int)(BitConverter.ToUInt32(hash, 0) % (uint)pool.Count)];
            }
        }
    }
}
