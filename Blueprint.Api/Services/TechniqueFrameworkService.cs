// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blueprint.Api.Services
{
    public interface ITechniqueFrameworkService
    {
        Task<IEnumerable<TechniqueFramework>> GetAsync(CancellationToken ct);
        Task<TechniqueFramework> GetAsync(Guid id, CancellationToken ct);
        Task<TechniqueFramework> CreateAsync(TechniqueFramework framework, CancellationToken ct);
        Task<TechniqueFramework> UpdateAsync(Guid id, TechniqueFramework framework, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    }

    /// <summary>
    /// The technique framework definitions an administrator has created or imported. A release keeps its techniques
    /// when its framework is changed or deleted; only new imports and what the release shows from its framework follow.
    /// </summary>
    public class TechniqueFrameworkService : ITechniqueFrameworkService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;
        private readonly ILogger<ITechniqueFrameworkService> _logger;

        public TechniqueFrameworkService(BlueprintContext context, IPrincipal user, ILogger<ITechniqueFrameworkService> logger, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<IEnumerable<TechniqueFramework>> GetAsync(CancellationToken ct)
        {
            return await _context.TechniqueFrameworks
                .OrderBy(f => f.Name)
                .ProjectTo<TechniqueFramework>(_mapper.ConfigurationProvider)
                .ToListAsync(ct);
        }

        public async Task<TechniqueFramework> GetAsync(Guid id, CancellationToken ct)
        {
            var framework = await _context.TechniqueFrameworks
                .Where(f => f.Id == id)
                .ProjectTo<TechniqueFramework>(_mapper.ConfigurationProvider)
                .SingleOrDefaultAsync(ct);
            return framework ?? throw new EntityNotFoundException<TechniqueFramework>();
        }

        public async Task<TechniqueFramework> CreateAsync(TechniqueFramework framework, CancellationToken ct)
        {
            Validate(framework);
            var domain = framework.Domain.Trim();
            if (await _context.TechniqueFrameworks.AnyAsync(f => f.Domain == domain, ct))
                throw new ConflictException($"A technique framework for the {domain} domain already exists.");

            var entity = new TechniqueFrameworkEntity { Id = Guid.NewGuid(), CreatedBy = _user.GetId() };
            Apply(entity, framework);
            _context.TechniqueFrameworks.Add(entity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Technique framework {Name} ({Domain}) created by {UserId}", entity.Name, entity.Domain, _user.GetId());

            return await GetAsync(entity.Id, ct);
        }

        public async Task<TechniqueFramework> UpdateAsync(Guid id, TechniqueFramework framework, CancellationToken ct)
        {
            var entity = await _context.TechniqueFrameworks.SingleOrDefaultAsync(f => f.Id == id, ct)
                ?? throw new EntityNotFoundException<TechniqueFramework>();
            Validate(framework);

            var domain = framework.Domain.Trim();
            if (await _context.TechniqueFrameworks.AnyAsync(f => f.Id != id && f.Domain == domain, ct))
                throw new ConflictException($"A technique framework for the {domain} domain already exists.");
            // a release's domain and family are what MSELs pin and pool by, so a framework with releases keeps them
            if ((entity.Domain != domain || entity.IsDefensive != framework.IsDefensive)
                && await _context.TechniqueReleases.AnyAsync(r => r.TechniqueFrameworkId == id, ct))
                throw new ConflictException($"{entity.Name} has releases, so its domain and family cannot change.");

            Apply(entity, framework);
            entity.ModifiedBy = _user.GetId();
            entity.DateModified = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Technique framework {Name} ({Domain}) edited by {UserId}", entity.Name, entity.Domain, _user.GetId());

            return await GetAsync(id, ct);
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var entity = await _context.TechniqueFrameworks.SingleOrDefaultAsync(f => f.Id == id, ct)
                ?? throw new EntityNotFoundException<TechniqueFramework>();

            // its releases stay, with no framework
            _context.TechniqueFrameworks.Remove(entity);
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Technique framework {Name} ({Domain}) deleted by {UserId}", entity.Name, entity.Domain, _user.GetId());
            return true;
        }

        private static void Apply(TechniqueFrameworkEntity entity, TechniqueFramework framework)
        {
            entity.Name = framework.Name.Trim();
            entity.Description = Blank(framework.Description);
            entity.Domain = framework.Domain.Trim();
            entity.IsDefensive = framework.IsDefensive;
            entity.Format = framework.Format;
            entity.Settings = framework.Settings ?? new TechniqueFrameworkSettings();
        }

        /// <summary>A 400 naming the first thing the definition lacks or gets wrong.</summary>
        public static void Validate(TechniqueFramework framework)
        {
            if (framework == null)
                throw new BadRequestException("A technique framework is required.");
            if (string.IsNullOrWhiteSpace(framework.Name))
                throw new BadRequestException("A technique framework needs a name.");
            if (string.IsNullOrWhiteSpace(framework.Domain))
                throw new BadRequestException("A technique framework needs a domain.");
            if (!Enum.IsDefined(framework.Format))
                throw new BadRequestException("The file format is not one Blueprint reads.");

            var settings = framework.Settings ?? new TechniqueFrameworkSettings();
            if ((settings.Match ?? new TechniqueFrameworkMatch()).IsEmpty)
                throw new BadRequestException("A technique framework needs at least one match condition, so a file can be recognised as its release.");

            var match = settings.Match;
            if (framework.Format == TechniqueFileFormat.StixBundle)
            {
                if (Given(match.Property) || Given(match.OntologyTitleContains))
                    throw new BadRequestException("A STIX bundle framework's match conditions are its domain, reference source, id prefix or collection name.");
                var stix = settings.Stix ?? new StixFormatOptions();
                if (stix.Grouping == StixGrouping.Hierarchy
                    && (!Given(stix.IdSource) || !Given(stix.AbstractionProperty) || !Given(stix.GroupAbstraction) || !Given(stix.ParentRefsProperty)))
                    throw new BadRequestException("Grouping by hierarchy needs an id source, abstraction property, group abstraction and parent refs property.");
            }
            else
            {
                if (Given(match.StixDomain) || Given(match.ReferenceSource) || Given(match.IdPrefix) || Given(match.CollectionName))
                    throw new BadRequestException("A JSON-LD ontology framework's match conditions are a property or ontology title.");
                var jsonLd = settings.JsonLd ?? new JsonLdFormatOptions();
                if (!Given(jsonLd.IdProperty) || !Given(jsonLd.TechniqueClass) || !Given(jsonLd.TacticType))
                    throw new BadRequestException("A JSON-LD ontology framework needs an id property, technique class and tactic type.");
            }

            if (settings.Mappings != null && (!Given(settings.Mappings.SourceIdVariable) || !Given(settings.Mappings.TargetVariable)))
                throw new BadRequestException("A mapping definition needs its source id and target variables.");

            ThrowIfNotWebAddress(settings.SourceUrl, "The source URL");
            ThrowIfNotWebAddress(settings.TermsUrl, "The terms URL");
            ThrowIfNotWebAddress(settings.DownloadUrl, "The download URL");
            ThrowIfNotWebAddress(Fill(settings.TechniqueUrlTemplate), "The technique URL template");
            ThrowIfNotWebAddress(Fill(settings.TacticUrlTemplate), "The tactic URL template");
            ThrowIfNotWebAddress(Fill(settings.WeaknessUrlTemplate), "The weakness URL template");
            ThrowIfNotWebAddress(Fill(settings.Mappings?.DownloadUrlTemplate), "The mappings download URL template");
        }

        /// <summary>A template with its placeholders filled, to check the address it makes.</summary>
        private static string Fill(string template) =>
            Blank(template)?.Replace("{id}", "x").Replace("{sourceId}", "x").Replace("{fullId}", "x").Replace("{version}", "x");

        private static void ThrowIfNotWebAddress(string url, string what)
        {
            url = Blank(url);
            if (url != null && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
                throw new BadRequestException($"{what} must be an http or https address.");
        }

        private static bool Given(string value) => !string.IsNullOrWhiteSpace(value);

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
