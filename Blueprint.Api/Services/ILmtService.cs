// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Blueprint.Api.Services
{
    public interface ILmtService
    {
        Task<string> GetLmtResourceAsync(Guid mselId, CancellationToken ct);
    }
}
