// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;

namespace Blueprint.Api.Automation
{
    /// <summary>The <c>Automation</c> section of the settings: how Blueprint runs automation tasks.</summary>
    public class AutomationOptions
    {
        public const string SectionName = "Automation";

        /// <summary>Whether this instance schedules and runs tasks. Run it on one replica until the hub has a backplane.</summary>
        public bool WorkerEnabled { get; set; } = true;
        /// <summary>How many runs this instance works at once.</summary>
        public int MaxConcurrentRuns { get; set; } = 8;
        /// <summary>How many VMs one run works on at once.</summary>
        public int MaxConcurrentVmOperationsPerRun { get; set; } = 8;
        /// <summary>How often the dispatcher looks for runs that are due.</summary>
        public int SchedulerIntervalSeconds { get; set; } = 2;
        /// <summary>How long an instance holds a run before another may take it over.</summary>
        public int LeaseSeconds { get; set; } = 60;
        /// <summary>The time a task gets when it sets none.</summary>
        public int DefaultExpirationSeconds { get; set; } = 300;
        /// <summary>The most time any task gets.</summary>
        public int MaxExpirationSeconds { get; set; } = 3600;
        /// <summary>How late a task scheduled at its event time may still start; later, it is recorded as skipped.</summary>
        public int MissedScheduleGraceSeconds { get; set; } = 300;
        /// <summary>The most output kept for one result.</summary>
        public int MaxOutputBytes { get; set; } = 65536;
        /// <summary>The most VMs one task may run on.</summary>
        public int MaxVmsPerTask { get; set; } = 50;
        /// <summary>How many test runs of a task are kept.</summary>
        public int TestRunsKeptPerTask { get; set; } = 20;

        /// <summary>
        /// The keys that encrypt tasks' secret parameters, by key id, each a base64 AES key of 32 bytes. Keep them out of
        /// the database. A task saved with a secret parameter while none is set is refused.
        /// </summary>
        public Dictionary<string, string> SecretKeys { get; set; } = new();
        /// <summary>The key id new secrets are encrypted with; the first of <see cref="SecretKeys"/> when unset.</summary>
        public string ActiveSecretKeyId { get; set; }

        public AutomationHttpOptions Http { get; set; } = new();
        public AutomationSshOptions Ssh { get; set; } = new();
        public AutomationEmailOptions Email { get; set; } = new();
        public AutomationFilesOptions Files { get; set; } = new();
    }

    public class AutomationHttpOptions
    {
        /// <summary>
        /// The hosts an http task may call: names, or <c>*.domain</c> for a domain's hosts. When none are set, only the
        /// hosts of the Crucible APIs in ClientSettings are allowed.
        /// </summary>
        public List<string> AllowedHosts { get; set; } = new();
        /// <summary>
        /// The hosts an http task sends Blueprint's service account token to. When none are set, it is sent to none.
        /// Blueprint's own API never gets it.
        /// </summary>
        public List<string> TokenHosts { get; set; } = new();
        /// <summary>
        /// Lets an http task reach loopback addresses, as a development stack on one machine needs. Link-local
        /// addresses, cloud metadata endpoints among them, are refused whatever this says.
        /// </summary>
        public bool AllowLoopbackAddresses { get; set; }
        public int TimeoutSeconds { get; set; } = 90;
        public int MaxResponseBytes { get; set; } = 1048576;
        /// <summary>Values that replace <c>{name}</c> in an http task's header values.</summary>
        public Dictionary<string, string> HeaderReplacements { get; set; } = new();
    }

    public class AutomationSshOptions
    {
        /// <summary>The hosts an SSH task may connect to, as for http. When none are set, SSH tasks are refused.</summary>
        public List<string> AllowedHosts { get; set; } = new();
        public string DefaultPrivateKey { get; set; }
        public string DefaultPrivateKeyPath { get; set; }
        public string DefaultPrivateKeyPassphrase { get; set; }
        public int CommandTimeoutSeconds { get; set; } = 60;
    }

    public class AutomationEmailOptions
    {
        public string SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        /// <summary>
        /// Requires STARTTLS: a server that does not offer it is refused before the credentials are sent. When false, the
        /// connection uses TLS on port 465 and STARTTLS when the server offers it otherwise.
        /// </summary>
        public bool UseStartTls { get; set; } = true;
        public string SmtpUsername { get; set; }
        public string SmtpPassword { get; set; }
        public string DefaultFromAddress { get; set; }
        public bool AcceptAllCertificates { get; set; }
    }

    public class AutomationFilesOptions
    {
        /// <summary>The directory a file upload task's file is read from. Unset, file upload tasks are refused.</summary>
        public string LocalDirectory { get; set; }
    }
}
