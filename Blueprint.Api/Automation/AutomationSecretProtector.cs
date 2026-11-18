// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Blueprint.Api.Automation
{
    /// <summary>
    /// Encrypts and decrypts the secret parameters of automation tasks (passwords, private keys, headers). They are
    /// stored as a JSON object of parameter key to <c>enc:v1:&lt;key id&gt;:&lt;base64 nonce, ciphertext and tag&gt;</c>,
    /// AES-GCM with the parameter key as associated data, so a value cannot be moved to another parameter. The keys
    /// come from the <c>Automation:SecretKeys</c> setting, so a copy of the database alone reveals nothing.
    /// </summary>
    public interface IAutomationSecretProtector
    {
        /// <summary>Whether a key is configured; without one, a secret parameter cannot be saved.</summary>
        bool IsConfigured { get; }
        /// <summary>The stored form of a task's secrets, each encrypted; null for none.</summary>
        string Protect(IReadOnlyDictionary<string, string> secrets);
        /// <summary>
        /// A task's stored secrets with <paramref name="changes"/> applied: a value sets (encrypts) that secret, an empty
        /// one removes it, and a key the changes do not name keeps its stored value. Null for none left.
        /// </summary>
        string Update(string stored, IReadOnlyDictionary<string, string> changes);
        /// <summary>The stored secrets, each encrypted again with the active key (a value stored in the clear included).</summary>
        string Reprotect(string stored);
        /// <summary>A task's secrets, decrypted. A value stored before it was encrypted is returned as it is.</summary>
        Dictionary<string, string> Unprotect(string stored);
        /// <summary>The keys a task's stored secrets hold, without decrypting them.</summary>
        IReadOnlyList<string> Keys(string stored);
        /// <summary>Whether any of the stored values is not yet encrypted with a configured key.</summary>
        bool NeedsProtecting(string stored);
    }

    public class AutomationSecretProtector : IAutomationSecretProtector
    {
        private const string Prefix = "enc:v1:";
        private readonly IOptionsMonitor<AutomationOptions> _options;

        public AutomationSecretProtector(IOptionsMonitor<AutomationOptions> options)
        {
            _options = options;
        }

        public bool IsConfigured => ActiveKey() != null;

        public string Protect(IReadOnlyDictionary<string, string> secrets) => Update(null, secrets);

        public string Update(string stored, IReadOnlyDictionary<string, string> changes)
        {
            var values = new SortedDictionary<string, string>(Read(stored), StringComparer.Ordinal);
            foreach (var (name, value) in changes ?? new Dictionary<string, string>())
            {
                var existing = values.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    values.Remove(existing);
                if (!string.IsNullOrEmpty(value))
                    values[name] = Encrypt(RequireActiveKey(), name, value);
            }
            return values.Count == 0 ? null : JsonSerializer.Serialize(values);
        }

        public string Reprotect(string stored)
        {
            var secrets = Unprotect(stored);
            return Protect(secrets);
        }

        private (string Id, byte[] Key) RequireActiveKey() => ActiveKey() ?? throw new AutomationConfigurationException(
            "Automation:SecretKeys is not set, so Blueprint cannot store a task's passwords, keys or headers.");

        public Dictionary<string, string> Unprotect(string stored)
        {
            var values = Read(stored);
            var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in values)
                secrets[name] = value.StartsWith(Prefix, StringComparison.Ordinal) ? Decrypt(name, value) : value;
            return secrets;
        }

        public IReadOnlyList<string> Keys(string stored) => Read(stored).Keys.ToList();

        public bool NeedsProtecting(string stored)
        {
            var activeId = ActiveKey()?.Id;
            return activeId != null && Read(stored).Values.Any(v => !v.StartsWith(Prefix + activeId + ":", StringComparison.Ordinal));
        }

        private static Dictionary<string, string> Read(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? new Dictionary<string, string>();
        }

        private (string Id, byte[] Key)? ActiveKey()
        {
            var options = _options.CurrentValue;
            var keys = options.SecretKeys;
            if (keys == null || keys.Count == 0)
                return null;
            var id = !string.IsNullOrEmpty(options.ActiveSecretKeyId) ? options.ActiveSecretKeyId : keys.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            return keys.TryGetValue(id, out var encoded) ? (id, KeyBytes(id, encoded)) : throw new AutomationConfigurationException(
                $"Automation:ActiveSecretKeyId names {id}, which is not one of Automation:SecretKeys.");
        }

        private byte[] KeyFor(string id)
        {
            var keys = _options.CurrentValue.SecretKeys ?? new();
            return keys.TryGetValue(id, out var encoded) ? KeyBytes(id, encoded) : throw new AutomationConfigurationException(
                $"A task's secret was encrypted with key {id}, which is not one of Automation:SecretKeys.");
        }

        private static byte[] KeyBytes(string id, string encoded)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(encoded ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new AutomationConfigurationException($"Automation:SecretKeys:{id} is not base64.");
            }
            return key.Length == 32 ? key : throw new AutomationConfigurationException($"Automation:SecretKeys:{id} is not a 32 byte key.");
        }

        private static string Encrypt((string Id, byte[] Key) key, string name, string value)
        {
            var (keyId, keyBytes) = key;
            var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            var plaintext = Encoding.UTF8.GetBytes(value);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            using (var aes = new AesGcm(keyBytes, tag.Length))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(name));
            return Prefix + keyId + ":" + Convert.ToBase64String(nonce.Concat(ciphertext).Concat(tag).ToArray());
        }

        private string Decrypt(string name, string value)
        {
            var rest = value.Substring(Prefix.Length);
            var colon = rest.IndexOf(':');
            if (colon <= 0)
                throw new AutomationConfigurationException($"A task's {name} is not stored in a form Blueprint reads.");
            var key = KeyFor(rest.Substring(0, colon));
            var bytes = Convert.FromBase64String(rest.Substring(colon + 1));
            var nonceSize = AesGcm.NonceByteSizes.MaxSize;
            var tagSize = AesGcm.TagByteSizes.MaxSize;
            var nonce = bytes.AsSpan(0, nonceSize);
            var tag = bytes.AsSpan(bytes.Length - tagSize);
            var ciphertext = bytes.AsSpan(nonceSize, bytes.Length - nonceSize - tagSize);
            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, tagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(name));
            }
            catch (CryptographicException)
            {
                throw new AutomationConfigurationException($"A task's {name} could not be decrypted with its key.");
            }
            return Encoding.UTF8.GetString(plaintext);
        }
    }
}
