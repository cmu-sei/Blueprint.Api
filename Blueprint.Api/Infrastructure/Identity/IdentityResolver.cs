// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Security.Claims;
using Blueprint.Api.Infrastructure.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Blueprint.Api.Infrastructure.Identity
{
    public interface IIdentityResolver
    {
        ClaimsPrincipal GetClaimsPrincipal();
        Guid GetId();
    }

    public class IdentityResolver : IIdentityResolver
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAuthorizationService _authorizationService;

        public IdentityResolver(
            IHttpContextAccessor httpContextAccessor,
            IAuthorizationService authorizationService)
        {
            _httpContextAccessor = httpContextAccessor;
            _authorizationService = authorizationService;
        }

        public ClaimsPrincipal GetClaimsPrincipal()
        {
            return _httpContextAccessor?.HttpContext?.User;
        }

        public Guid GetId()
        {
            return this.GetClaimsPrincipal().GetId();
        }
    }
}
