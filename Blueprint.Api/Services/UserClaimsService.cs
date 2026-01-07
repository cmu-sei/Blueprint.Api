// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Options;

namespace Blueprint.Api.Services
{
    public interface IUserClaimsService
    {
        Task<ClaimsPrincipal> AddUserClaims(ClaimsPrincipal principal, bool update);
        Task<ClaimsPrincipal> GetClaimsPrincipal(Guid userId, bool setAsCurrent);
        Task<ClaimsPrincipal> RefreshClaims(Guid userId);
        ClaimsPrincipal GetCurrentClaimsPrincipal();
        void SetCurrentClaimsPrincipal(ClaimsPrincipal principal);
    }

    public class UserClaimsService : IUserClaimsService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsTransformationOptions _options;
        private IMemoryCache _cache;
        private ClaimsPrincipal _currentClaimsPrincipal;

        public UserClaimsService(BlueprintContext context, IMemoryCache cache, ClaimsTransformationOptions options)
        {
            _context = context;
            _options = options;
            _cache = cache;
        }

        public async Task<ClaimsPrincipal> AddUserClaims(ClaimsPrincipal principal, bool update)
        {
            List<Claim> claims;
            var identity = ((ClaimsIdentity)principal.Identity);
            var userId = principal.GetId();

            // Don't use cached claims if given a new token and we are using roles or groups from the token
            if (_cache.TryGetValue(userId, out claims) && (_options.UseGroupsFromIdP || _options.UseRolesFromIdP))
            {
                var cachedTokenId = claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;
                var newTokenId = identity.Claims.FirstOrDefault(x => x.Type == JwtRegisteredClaimNames.Jti)?.Value;

                if (newTokenId != cachedTokenId)
                {
                    claims = null;
                }
            }

            if (claims == null)
            {
                claims = new List<Claim>();
                var user = await ValidateUser(userId, principal.FindFirst("name")?.Value, update);

                if (user != null)
                {
                    // Preserve JWT ID to detect token changes
                    var jtiClaim = identity.Claims.Where(x => x.Type == JwtRegisteredClaimNames.Jti).FirstOrDefault();
                    if (jtiClaim is not null)
                    {
                        claims.Add(new Claim(jtiClaim.Type, jtiClaim.Value));
                    }

                    claims.AddRange(await GetPermissionClaims(userId, principal));

                    if (_options.EnableCaching)
                    {
                        _cache.Set(userId, claims, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(_options.CacheExpirationSeconds)));
                    }
                }
            }
            addNewClaims(identity, claims);
            return principal;
        }

        public async Task<ClaimsPrincipal> GetClaimsPrincipal(Guid userId, bool setAsCurrent)
        {
            ClaimsIdentity identity = new ClaimsIdentity();
            identity.AddClaim(new Claim("sub", userId.ToString()));
            ClaimsPrincipal principal = new ClaimsPrincipal(identity);

            principal = await AddUserClaims(principal, false);

            if (setAsCurrent || _currentClaimsPrincipal.GetId() == userId)
            {
                _currentClaimsPrincipal = principal;
            }

            return principal;
        }

        public async Task<ClaimsPrincipal> RefreshClaims(Guid userId)
        {
            _cache.Remove(userId);
            return await GetClaimsPrincipal(userId, false);
        }

        public ClaimsPrincipal GetCurrentClaimsPrincipal()
        {
            return _currentClaimsPrincipal;
        }

        public void SetCurrentClaimsPrincipal(ClaimsPrincipal principal)
        {
            _currentClaimsPrincipal = principal;
        }

        private async Task<UserEntity> ValidateUser(Guid subClaim, string nameClaim, bool update)
        {
            var user = await _context.Users
                .Where(u => u.Id == subClaim)
                .SingleOrDefaultAsync();

            var anyUsers = await _context.Users.AnyAsync();

            if (update)
            {
                if (user == null)
                {
                    user = new UserEntity
                    {
                        Id = subClaim,
                        Name = nameClaim ?? "Anonymous",
                        CreatedBy = subClaim
                    };

                    // First user is default SystemAdmin
                    if (!anyUsers)
                    {
                        var systemAdminPermission = await _context.Permissions.FirstOrDefaultAsync(p => p.Key == BlueprintClaimTypes.SystemAdmin.ToString());

                        if (systemAdminPermission != null)
                        {
                            user.UserPermissions.Add(new UserPermissionEntity(user.Id, systemAdminPermission.Id));
                        }
                    }

                    _context.Users.Add(user);
                    await _context.SaveChangesAsync();
                }
                else
                {
                    if (nameClaim != null && user.Name != nameClaim)
                    {
                        user.Name = nameClaim;
                        _context.Update(user);
                        await _context.SaveChangesAsync();
                    }
                }
            }

            return user;
        }

        private async Task<IEnumerable<Claim>> GetPermissionClaims(Guid userId, ClaimsPrincipal principal)
        {
            List<Claim> claims = new List<Claim>();

            // Get legacy UserPermissions (Blueprint claim types like SystemAdmin, ContentDeveloper)
            var userPermissions = await _context.UserPermissions
                .Where(u => u.UserId == userId)
                .Include(x => x.Permission)
                .ToArrayAsync();

            foreach (var userPermission in userPermissions)
            {
                BlueprintClaimTypes blueprintClaim;
                if (Enum.TryParse<BlueprintClaimTypes>(userPermission.Permission.Key, out blueprintClaim))
                {
                    claims.Add(new Claim(blueprintClaim.ToString(), "true"));
                }
            }

            // Extract roles from IdP token if configured
            var tokenRoleNames = _options.UseRolesFromIdP
                ? GetClaimsFromToken(principal, _options.RolesClaimPath).Select(x => x.ToLower())
                : Enumerable.Empty<string>();

            // Look up system roles in database that match token role names
            var roles = await _context.SystemRoles
                .Where(x => tokenRoleNames.Contains(x.Name.ToLower()))
                .ToListAsync();

            // Add user's assigned role (if any)
            var userRole = await _context.Users
                .Where(x => x.Id == userId)
                .Select(x => x.Role)
                .FirstOrDefaultAsync();

            if (userRole != null)
            {
                roles.Add(userRole);
            }

            roles = roles.Distinct().ToList();

            // Build permission claims from matched roles
            foreach (var role in roles)
            {
                List<string> permissions;

                if (role.AllPermissions)
                {
                    permissions = Enum.GetValues<SystemPermission>().Select(x => x.ToString()).ToList();
                }
                else
                {
                    permissions = role.Permissions?.Select(x => x.ToString()).ToList() ?? new List<string>();
                }

                foreach (var permission in permissions)
                {
                    if (!claims.Any(x => x.Type == AuthorizationConstants.PermissionClaimType && x.Value == permission))
                    {
                        claims.Add(new Claim(AuthorizationConstants.PermissionClaimType, permission));
                    }
                }
            }

            // Extract groups from IdP token if configured
            var groupNames = _options.UseGroupsFromIdP
                ? GetClaimsFromToken(principal, _options.GroupsClaimPath).Select(x => x.ToLower())
                : Enumerable.Empty<string>();

            // Find groups in database (by membership or IdP name match)
            var groupIds = await _context.Groups
                .Where(x => x.Memberships.Any(y => y.UserId == userId) || groupNames.Contains(x.Name.ToLower()))
                .Select(x => x.Id)
                .ToListAsync();

            // Note: Additional group-based permission logic can be added here
            // For example, MSEL access based on group membership

            return claims;
        }

        private string[] GetClaimsFromToken(ClaimsPrincipal principal, string claimPath)
        {
            if (string.IsNullOrEmpty(claimPath))
            {
                return Array.Empty<string>();
            }

            // Parse nested JSON paths like "realm_access.roles" or "address.street"
            var pathSegments = Regex.Split(claimPath, @"(?<!\\)\.").Select(s => s.Replace("\\.", ".")).ToArray();

            var tokenClaim = principal.Claims.Where(x => x.Type == pathSegments.First()).FirstOrDefault();

            if (tokenClaim == null)
            {
                return Array.Empty<string>();
            }

            // Handle both string and JSON value types
            return tokenClaim.ValueType switch
            {
                ClaimValueTypes.String => new[] { tokenClaim.Value },
                "JSON" => ExtractJsonClaimValues(tokenClaim.Value, pathSegments.Skip(1)),
                _ => Array.Empty<string>()
            };
        }

        private string[] ExtractJsonClaimValues(string json, IEnumerable<string> pathSegments)
        {
            List<string> values = new List<string>();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement currentElement = doc.RootElement;

                // Navigate through nested JSON properties
                foreach (var segment in pathSegments)
                {
                    if (!currentElement.TryGetProperty(segment, out JsonElement propertyElement))
                    {
                        return Array.Empty<string>();
                    }
                    currentElement = propertyElement;
                }

                // Extract values (handles both arrays and single strings)
                if (currentElement.ValueKind == JsonValueKind.Array)
                {
                    values.AddRange(currentElement.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()));
                }
                else if (currentElement.ValueKind == JsonValueKind.String)
                {
                    values.Add(currentElement.GetString());
                }
            }
            catch (JsonException)
            {
                // Handle invalid JSON format silently
            }

            return values.ToArray();
        }

        private void addNewClaims(ClaimsIdentity identity, List<Claim> claims)
        {
            var newClaims = new List<Claim>();
            claims.ForEach(delegate (Claim claim)
            {
                if (!identity.Claims.Any(identityClaim => identityClaim.Type == claim.Type))
                {
                    newClaims.Add(claim);
                }
            });
            identity.AddClaims(newClaims);
        }
    }
}
