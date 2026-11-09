// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

namespace Blueprint.Api.Infrastructure.Options
{
    public class XApiOptions
    {
        public bool Enabled { get; set; }
        public string Endpoint { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string IssuerUrl { get; set; }
        public string ApiUrl { get; set; }
        public string UiUrl { get; set; }
        public string Platform { get; set; }
        public string EmailDomain { get; set; }
        public int RetentionDays { get; set; } = 7;
        public int ProcessingTimeoutMinutes { get; set; } = 10;
        public int ProcessingDelaySeconds { get; set; } = 30;

        public const int DefaultMaxStatementsPerRegistration = 5000;

        /// <summary>
        /// The most statements the Assessor View reads from the LRS for each registration (the MSEL and each
        /// integration it is pushed to) before it stops following the LRS's <c>more</c> links. Zero or less
        /// means the default.
        /// </summary>
        public int MaxStatementsPerRegistration { get; set; } = DefaultMaxStatementsPerRegistration;
    }
}
