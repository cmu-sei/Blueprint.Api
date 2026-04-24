// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Infrastructure.Options;

namespace Blueprint.Api.Services
{
    public class LmtService : ILmtService
    {
        private readonly BlueprintContext _context;
        private readonly ClientOptions _clientOptions;

        public LmtService(BlueprintContext context, ClientOptions clientOptions)
        {
            _context = context;
            _clientOptions = clientOptions;
        }

        public async Task<string> GetLmtResourceAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == mselId, ct);

            if (msel == null)
                return null;

            // Load MSEL competencies with framework and competency details
            var mselCompetencies = await _context.MselCompetencies
                .AsNoTracking()
                .Include(mc => mc.Competency)
                    .ThenInclude(c => c.CompetencyFramework)
                .Where(mc => mc.MselId == mselId)
                .ToListAsync(ct);

            var baseUrl = _clientOptions.BlueprintApiUrl?.TrimEnd('/') ?? "http://localhost:4724/api";
            var uiUrl = _clientOptions.BlueprintUiUrl?.TrimEnd('/') ?? "http://localhost:4725";

            // Build JSON-LD object
            var lmt = new
            {
                context = "https://schema.org/",
                type = "LearningResource",
                id = $"{baseUrl}/lmt/resource/{msel.Id}",
                url = $"{uiUrl}/#/msel/{msel.Id}",
                name = msel.Name,
                description = msel.Description ?? "",

                // lrmi:educationalLevel (Beginner/Intermediate/Advanced)
                educationalLevel = msel.EducationalLevel ?? "",

                // lrmi:educationalUse (Assessment, Instruction, Professional Support)
                educationalUse = msel.EducationalUse ?? "Assessment",

                // schema:courseMode (Online, Onsite, Blended)
                courseMode = msel.CourseMode ?? "Online",

                // schema:inLanguage
                inLanguage = msel.Language ?? "en-US",

                // dcterms:subject (topics)
                subject = string.IsNullOrWhiteSpace(msel.Subject)
                    ? Array.Empty<string>()
                    : msel.Subject.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray(),

                // schema:keywords
                keywords = string.IsNullOrWhiteSpace(msel.Keywords)
                    ? Array.Empty<string>()
                    : msel.Keywords.Split(',').Select(k => k.Trim()).Where(k => !string.IsNullOrEmpty(k)).ToArray(),

                // lrmi:assesses - competencies this exercise assesses
                assesses = mselCompetencies
                    .Where(mc => mc.Competency != null)
                    .Select(mc => new
                    {
                        type = "DefinedTerm",
                        // Use competency IRI if it's HTTP, else use API-relative URL
                        id = (mc.Competency.IdNumber?.StartsWith("http") == true)
                            ? mc.Competency.IdNumber
                            : $"{baseUrl}/competencies/{mc.CompetencyId}",
                        name = mc.Competency.ShortName ?? mc.Competency.IdNumber,
                        inDefinedTermSet = (mc.Competency.CompetencyFramework?.IdNumber?.StartsWith("http") == true)
                            ? mc.Competency.CompetencyFramework.IdNumber
                            : $"{baseUrl}/competency-frameworks/{mc.Competency.CompetencyFrameworkId}"
                    })
                    .ToArray(),

                // schema:typicalAgeRange (if we add it later)
                // schema:timeRequired (exercise duration - not yet captured in MSEL entity)

                // schema:provider (the organization providing the exercise)
                provider = new
                {
                    type = "Organization",
                    name = "Carnegie Mellon University Software Engineering Institute",
                    url = "https://www.sei.cmu.edu/our-work/projects/display.cfm?customel_datapageid_4050=6278"
                },

                // schema:publisher (same as provider for Crucible)
                publisher = new
                {
                    type = "Organization",
                    name = "Carnegie Mellon University Software Engineering Institute",
                    url = "https://www.sei.cmu.edu/our-work/projects/display.cfm?customel_datapageid_4050=6278"
                }
            };

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };

            return JsonSerializer.Serialize(lmt, options);
        }
    }
}
