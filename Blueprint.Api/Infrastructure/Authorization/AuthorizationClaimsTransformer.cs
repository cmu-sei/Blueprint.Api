// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System.Security.Claims;
using System.Threading.Tasks;
using Blueprint.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Blueprint.Api.Infrastructure.Extensions;

namespace Blueprint.Api.Infrastructure.Authorization
{
    class AuthorizationClaimsTransformer : IClaimsTransformation
    {
        private IUserClaimsService _claimsService;

        public AuthorizationClaimsTransformer(IUserClaimsService claimsService)
        {
            _claimsService = claimsService;
        }

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var user = principal.NormalizeScopeClaims();
            user = await _claimsService.AddUserClaims(user, true);
            _claimsService.SetCurrentClaimsPrincipal(user);
            return user;
        }
    }
}

