// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Blueprint.Api.Services
{
    public interface ICompetencyFrameworkService
    {
        Task<IEnumerable<ViewModels.CompetencyFramework>> GetAsync(CancellationToken ct);
        Task<ViewModels.CompetencyFramework> GetAsync(Guid id, CancellationToken ct);
        Task<ViewModels.CompetencyFramework> CreateAsync(ViewModels.CompetencyFramework framework, CancellationToken ct);
        Task<ViewModels.CompetencyFramework> UpdateAsync(Guid id, ViewModels.CompetencyFramework framework, CancellationToken ct);
        Task<ViewModels.CompetencyFramework> ImportFromMoodleCsvAsync(Stream csvStream, string source, string version, CancellationToken ct);
        Task<ViewModels.CompetencyFramework> ImportFromNiceJsonAsync(Stream jsonStream, CancellationToken ct);
        Task<ViewModels.CompetencyFramework> ImportFromDcwfXlsxAsync(Stream xlsxStream, string source, string version, CancellationToken ct);
        Task<ViewModels.CompetencyFrameworkImportPreview> PreviewCsvAsync(Stream csvStream, string source, string version, CancellationToken ct);
        Task<ViewModels.CompetencyFrameworkImportPreview> PreviewJsonAsync(Stream jsonStream, CancellationToken ct);
        Task<ViewModels.CompetencyFrameworkImportPreview> PreviewXlsxAsync(Stream xlsxStream, string source, string version, CancellationToken ct);
        Task<ViewModels.FrameworkDeleteCheck> CheckCanDeleteAsync(Guid id, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct);
        Task<ViewModels.Competency> CreateCompetencyAsync(Guid frameworkId, ViewModels.Competency competency, CancellationToken ct);
        Task<ViewModels.Competency> UpdateCompetencyAsync(Guid competencyId, ViewModels.Competency competency, CancellationToken ct);
        Task<bool> DeleteCompetencyAsync(Guid competencyId, CancellationToken ct);
    }

    public class CompetencyFrameworkService : ICompetencyFrameworkService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public CompetencyFrameworkService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.CompetencyFramework>> GetAsync(CancellationToken ct)
        {
            var items = await _context.CompetencyFrameworks
                .AsNoTracking()
                .ToListAsync(ct);

            return _mapper.Map<IEnumerable<ViewModels.CompetencyFramework>>(items);
        }

        public async Task<ViewModels.CompetencyFramework> GetAsync(Guid id, CancellationToken ct)
        {
            var framework = await _context.CompetencyFrameworks
                .AsNoTracking()
                .AsSplitQuery()
                .Include(f => f.Competencies)
                    .ThenInclude(c => c.Relationships)
                .SingleOrDefaultAsync(f => f.Id == id, ct);

            if (framework == null)
                throw new EntityNotFoundException<ViewModels.CompetencyFramework>();

            var result = _mapper.Map<ViewModels.CompetencyFramework>(framework);

            // Populate RelatedIdNumbers on each competency view model
            var idNumberMap = framework.Competencies.ToDictionary(c => c.Id, c => c.IdNumber);
            foreach (var comp in result.Competencies)
            {
                var entity = framework.Competencies.First(c => c.Id == comp.Id);
                var relatedIds = entity.Relationships
                    .Select(r => idNumberMap.GetValueOrDefault(r.RelatedCompetencyId))
                    .Where(n => n != null)
                    .ToList();
                // Also include inverse relationships
                var inverseRelated = framework.Competencies
                    .SelectMany(c => c.Relationships)
                    .Where(r => r.RelatedCompetencyId == comp.Id)
                    .Select(r => idNumberMap.GetValueOrDefault(r.CompetencyId))
                    .Where(n => n != null);
                comp.RelatedIdNumbers = relatedIds.Union(inverseRelated).Distinct().ToList();
            }

            return result;
        }

        private const int BatchSize = 500;

        public async Task<ViewModels.CompetencyFramework> CreateAsync(ViewModels.CompetencyFramework framework, CancellationToken ct)
        {
            var userId = _user.GetId();
            var entity = _mapper.Map<CompetencyFrameworkEntity>(framework);
            entity.Id = Guid.NewGuid();
            entity.CreatedBy = userId;

            // Detach competencies and relationships so we can batch them separately
            var competencies = entity.Competencies?.ToList() ?? new List<CompetencyEntity>();
            entity.Competencies = new HashSet<CompetencyEntity>();

            using var transaction = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                // 1. Save framework only
                _context.CompetencyFrameworks.Add(entity);
                await _context.SaveChangesAsync(ct);

                // 2. Batch-insert competencies
                var allRelationships = new List<CompetencyRelationshipEntity>();
                foreach (var batch in competencies.Chunk(BatchSize))
                {
                    foreach (var comp in batch)
                    {
                        comp.CompetencyFrameworkId = entity.Id;
                        if (comp.Relationships != null)
                        {
                            allRelationships.AddRange(comp.Relationships);
                            comp.Relationships = new HashSet<CompetencyRelationshipEntity>();
                        }
                        _context.Competencies.Add(comp);
                    }
                    await _context.SaveChangesAsync(ct);
                }

                // 3. Batch-insert relationships from entity mappings
                foreach (var batch in allRelationships.Chunk(BatchSize))
                {
                    _context.CompetencyRelationships.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                }

                // 4. Resolve relatedIdNumbers from view model into relationship entities
                var idNumberToGuid = new Dictionary<string, Guid>();
                foreach (var c in competencies.Where(c => c.IdNumber != null))
                {
                    idNumberToGuid.TryAdd(c.IdNumber, c.Id);
                }
                var vmCompetencies = framework.Competencies ?? new List<ViewModels.Competency>();
                var resolvedRelationships = new List<CompetencyRelationshipEntity>();
                var seenPairs = new HashSet<(Guid, Guid)>();
                foreach (var vm in vmCompetencies)
                {
                    if (vm.RelatedIdNumbers == null || vm.RelatedIdNumbers.Count == 0 || string.IsNullOrEmpty(vm.IdNumber))
                        continue;
                    if (!idNumberToGuid.TryGetValue(vm.IdNumber, out var sourceGuid))
                        continue;
                    foreach (var relatedId in vm.RelatedIdNumbers)
                    {
                        if (idNumberToGuid.TryGetValue(relatedId, out var destGuid) && seenPairs.Add((sourceGuid, destGuid)))
                        {
                            resolvedRelationships.Add(new CompetencyRelationshipEntity
                            {
                                Id = Guid.NewGuid(),
                                CompetencyId = sourceGuid,
                                RelatedCompetencyId = destGuid,
                                CreatedBy = userId
                            });
                        }
                    }
                }
                foreach (var batch in resolvedRelationships.Chunk(BatchSize))
                {
                    _context.CompetencyRelationships.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                throw new ArgumentException(
                    $"Database error creating framework: {ex.InnerException?.Message ?? ex.Message}");
            }

            return await GetAsync(entity.Id, ct);
        }

        public async Task<ViewModels.CompetencyFramework> UpdateAsync(Guid id, ViewModels.CompetencyFramework framework, CancellationToken ct)
        {
            var entity = await _context.CompetencyFrameworks
                .SingleOrDefaultAsync(f => f.Id == id, ct);

            if (entity == null)
                throw new EntityNotFoundException<ViewModels.CompetencyFramework>();

            entity.Name = framework.Name;
            entity.Description = framework.Description;
            entity.Source = framework.Source;
            entity.Version = framework.Version;
            entity.DefaultProficiencyScaleId = framework.DefaultProficiencyScaleId;
            entity.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);
            return await GetAsync(id, ct);
        }

        public async Task<ViewModels.CompetencyFramework> ImportFromMoodleCsvAsync(Stream csvStream, string source, string version, CancellationToken ct)
        {
            var userId = _user.GetId();
            var rows = ParseMoodleCsv(csvStream);

            if (rows.Count == 0)
                throw new ArgumentException("CSV file is empty or has no data rows.");

            // First row with IsFramework = true is the framework definition
            var frameworkRow = rows.FirstOrDefault(r => r.IsFramework);
            if (frameworkRow == null)
                throw new ArgumentException("CSV does not contain a framework row (Is Framework = 1).");

            // Check for duplicate framework
            var existingFramework = await _context.CompetencyFrameworks
                .FirstOrDefaultAsync(f => f.IdNumber == frameworkRow.IdNumber, ct);
            if (existingFramework != null)
                throw new ArgumentException($"A framework with ID number '{frameworkRow.IdNumber}' already exists.");

            var frameworkEntity = new CompetencyFrameworkEntity
            {
                Id = Guid.NewGuid(),
                Name = frameworkRow.ShortName,
                IdNumber = frameworkRow.IdNumber,
                Description = frameworkRow.Description,
                DescriptionFormat = frameworkRow.DescriptionFormat,
                Source = source ?? "",
                Version = version ?? "",
                ScaleValues = frameworkRow.ScaleValues,
                ScaleConfiguration = frameworkRow.ScaleConfiguration,
                Taxonomies = frameworkRow.Taxonomy,
                CreatedBy = userId
            };

            using var transaction = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                // 1. Save framework first
                _context.CompetencyFrameworks.Add(frameworkEntity);
                await _context.SaveChangesAsync(ct);

                // Build competency entities from non-framework rows (in memory, not tracked yet)
                var competencyRows = rows.Where(r => !r.IsFramework).ToList();
                var idNumberToEntity = new Dictionary<string, CompetencyEntity>();
                int sortOrder = 0;

                foreach (var row in competencyRows)
                {
                    if (string.IsNullOrWhiteSpace(row.IdNumber) || string.IsNullOrWhiteSpace(row.ShortName))
                        continue;

                    if (idNumberToEntity.ContainsKey(row.IdNumber))
                        continue;

                    var entity = new CompetencyEntity
                    {
                        Id = Guid.NewGuid(),
                        CompetencyFrameworkId = frameworkEntity.Id,
                        IdNumber = row.IdNumber,
                        ShortName = StripIdPrefix(row.ShortName, row.IdNumber),
                        Description = row.Description,
                        DescriptionFormat = row.DescriptionFormat,
                        SortOrder = sortOrder++,
                        RuleType = row.RuleType,
                        RuleOutcome = ParseInt(row.RuleOutcome),
                        RuleConfig = row.RuleConfig,
                        ScaleValues = row.ScaleValues,
                        ScaleConfiguration = row.ScaleConfiguration,
                        CreatedBy = userId
                    };

                    idNumberToEntity[row.IdNumber] = entity;
                }

                // 2. Batch-insert competencies (without parents to avoid FK ordering issues)
                foreach (var batch in idNumberToEntity.Values.Chunk(BatchSize))
                {
                    _context.Competencies.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                }

                // 2b. Set parent references and build paths now that all competencies exist
                foreach (var row in competencyRows)
                {
                    if (!idNumberToEntity.ContainsKey(row.IdNumber))
                        continue;

                    var entity = idNumberToEntity[row.IdNumber];

                    if (!string.IsNullOrWhiteSpace(row.ParentIdNumber) && idNumberToEntity.ContainsKey(row.ParentIdNumber))
                    {
                        entity.ParentId = idNumberToEntity[row.ParentIdNumber].Id;
                    }
                }

                var byId = idNumberToEntity.Values.ToDictionary(e => e.Id);
                foreach (var entity in idNumberToEntity.Values)
                {
                    entity.Path = BuildPath(entity, byId);
                }
                await _context.SaveChangesAsync(ct);

                // Third pass: create cross-reference relationships in batches
                var seenPairs = new HashSet<(Guid, Guid)>();
                var relationships = new List<CompetencyRelationshipEntity>();
                foreach (var row in competencyRows)
                {
                    if (string.IsNullOrWhiteSpace(row.RelatedIdNumbers) || !idNumberToEntity.ContainsKey(row.IdNumber))
                        continue;

                    var entity = idNumberToEntity[row.IdNumber];
                    var relatedIds = row.RelatedIdNumbers
                        .Replace("%2C", "\x01")
                        .Split(',')
                        .Select(s => s.Replace("\x01", ",").Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct();

                    foreach (var relatedIdNumber in relatedIds)
                    {
                        if (!idNumberToEntity.ContainsKey(relatedIdNumber))
                            continue;

                        var relatedEntity = idNumberToEntity[relatedIdNumber];
                        var pair = (entity.Id, relatedEntity.Id);
                        if (!seenPairs.Add(pair))
                            continue;

                        relationships.Add(new CompetencyRelationshipEntity
                        {
                            Id = Guid.NewGuid(),
                            CompetencyId = entity.Id,
                            RelatedCompetencyId = relatedEntity.Id,
                            CreatedBy = userId
                        });
                    }
                }

                // 3. Batch-insert relationships
                foreach (var batch in relationships.Chunk(BatchSize))
                {
                    _context.CompetencyRelationships.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                throw new ArgumentException(
                    $"Database error importing framework: {ex.InnerException?.Message ?? ex.Message}");
            }

            return await GetAsync(frameworkEntity.Id, ct);
        }

        public async Task<ViewModels.CompetencyFramework> ImportFromNiceJsonAsync(Stream jsonStream, CancellationToken ct)
        {
            var userId = _user.GetId();
            var jsonDoc = await JsonSerializer.DeserializeAsync<JsonElement>(jsonStream, cancellationToken: ct);

            // Navigate to the container with {documents, elements, relationships}
            // Supports two formats:
            //   Wrapped:  { response: { elements: { documents, elements, relationships } } }
            //   Flat:     { documents, elements, relationships }
            var root = jsonDoc;
            if (root.TryGetProperty("response", out var response))
                root = response;
            if (root.TryGetProperty("elements", out var elementsCandidate) &&
                elementsCandidate.ValueKind == JsonValueKind.Object)
                root = elementsCandidate;

            // Parse document metadata
            var documents = root.GetProperty("documents");
            var doc = documents.EnumerateArray().FirstOrDefault();
            var docName = doc.TryGetProperty("name", out var n) ? n.GetString() : "Imported Framework";
            var docVersion = doc.TryGetProperty("version", out var v) ? v.GetString() : "";
            var docSource = doc.TryGetProperty("doc_identifier", out var d) ? d.GetString() : "";

            // Check for duplicate
            var existingFramework = await _context.CompetencyFrameworks
                .FirstOrDefaultAsync(f => f.Source == docSource && f.Version == docVersion, ct);
            if (existingFramework != null)
                throw new ArgumentException($"A framework with source '{docSource}' version '{docVersion}' already exists.");

            // Parse elements
            var elements = root.GetProperty("elements");
            var skippedTypes = new HashSet<string> { "sort", "opm_code" };

            var frameworkEntity = new CompetencyFrameworkEntity
            {
                Id = Guid.NewGuid(),
                Name = docName,
                IdNumber = docSource,
                Description = $"Imported from {docSource}",
                Source = docSource,
                Version = docVersion,
                CreatedBy = userId
            };

            var idNumberToEntity = new Dictionary<string, CompetencyEntity>();
            var idNumberToType = new Dictionary<string, string>();
            int sortOrder = 0;

            foreach (var el in elements.EnumerateArray())
            {
                var elementType = el.GetProperty("element_type").GetString();
                if (skippedTypes.Contains(elementType))
                    continue;

                var identifier = el.GetProperty("element_identifier").GetString();
                var title = el.TryGetProperty("title", out var t) ? t.GetString() : "";
                var text = el.TryGetProperty("text", out var tx) ? tx.GetString() : "";

                var shortName = !string.IsNullOrWhiteSpace(title) && title != "N/A"
                    ? title
                    : !string.IsNullOrWhiteSpace(text) ? text : identifier;

                var entity = new CompetencyEntity
                {
                    Id = Guid.NewGuid(),
                    CompetencyFrameworkId = frameworkEntity.Id,
                    IdNumber = identifier,
                    ShortName = shortName,
                    Description = text ?? "",
                    SortOrder = sortOrder++,
                    CreatedBy = userId
                };

                idNumberToEntity[identifier] = entity;
                idNumberToType[identifier] = elementType;
            }

            // Hierarchical types: relationships between these define parent-child, not cross-references
            // Covers both 2017 format ("category", "specialty area", "work role")
            // and 2.x format ("category", "competency_area", "work_role")
            var hierarchyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "category", "specialty area", "competency_area", "work_role", "work role"
            };

            // Parse relationships: hierarchy links vs cross-references
            var relationships = root.GetProperty("relationships");
            var parentLinks = new List<(CompetencyEntity child, Guid parentId)>();
            var crossRefs = new List<(string sourceId, string destId)>();

            foreach (var rel in relationships.EnumerateArray())
            {
                var sourceId = rel.GetProperty("source_element_identifier").GetString();
                var destId = rel.GetProperty("dest_element_identifier").GetString();

                if (!idNumberToEntity.TryGetValue(sourceId, out var sourceEntity) ||
                    !idNumberToEntity.TryGetValue(destId, out var destEntity))
                    continue;

                var sourceType = idNumberToType.GetValueOrDefault(sourceId, "");
                var destType = idNumberToType.GetValueOrDefault(destId, "");

                // If both source and dest are hierarchy types, this is a parent-child link
                if (hierarchyTypes.Contains(sourceType) && hierarchyTypes.Contains(destType))
                {
                    parentLinks.Add((destEntity, sourceEntity.Id));
                }
                else
                {
                    crossRefs.Add((sourceId, destId));
                }
            }

            using var transaction = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                // 1. Save framework
                _context.CompetencyFrameworks.Add(frameworkEntity);
                await _context.SaveChangesAsync(ct);

                // 2. Batch-insert competencies (without parents to avoid FK ordering issues)
                foreach (var batch in idNumberToEntity.Values.Chunk(BatchSize))
                {
                    _context.Competencies.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                }

                // 3. Set parent relationships and build paths now that all competencies exist
                foreach (var (child, parentId) in parentLinks)
                {
                    child.ParentId = parentId;
                }
                var byId = idNumberToEntity.Values.ToDictionary(e => e.Id);
                foreach (var entity in idNumberToEntity.Values)
                {
                    entity.Path = BuildPath(entity, byId);
                }
                await _context.SaveChangesAsync(ct);

                // 4. Batch-insert cross-reference relationships
                var relationshipEntities = new List<CompetencyRelationshipEntity>();
                foreach (var (sourceId, destId) in crossRefs)
                {
                    if (idNumberToEntity.TryGetValue(sourceId, out var src) &&
                        idNumberToEntity.TryGetValue(destId, out var dst))
                    {
                        relationshipEntities.Add(new CompetencyRelationshipEntity
                        {
                            Id = Guid.NewGuid(),
                            CompetencyId = src.Id,
                            RelatedCompetencyId = dst.Id,
                            CreatedBy = userId
                        });
                    }
                }

                foreach (var batch in relationshipEntities.Chunk(BatchSize))
                {
                    _context.CompetencyRelationships.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                throw new ArgumentException(
                    $"Database error importing framework: {ex.InnerException?.Message ?? ex.Message}");
            }

            return await GetAsync(frameworkEntity.Id, ct);
        }

        public async Task<ViewModels.CompetencyFramework> ImportFromDcwfXlsxAsync(Stream xlsxStream, string source, string version, CancellationToken ct)
        {
            var userId = _user.GetId();

            // Check for duplicate
            var existingFramework = await _context.CompetencyFrameworks
                .FirstOrDefaultAsync(f => f.Source == source && f.Version == version, ct);
            if (existingFramework != null)
                throw new ArgumentException($"A framework with source '{source}' version '{version}' already exists.");

            using var document = SpreadsheetDocument.Open(xlsxStream, false);
            var workbookPart = document.WorkbookPart;
            var sheets = workbookPart.Workbook.Sheets.Elements<Sheet>().ToList();

            // Find required sheets
            var rolesSheet = sheets.FirstOrDefault(s => s.Name == "DCWF Roles");
            var tasksSheet = sheets.FirstOrDefault(s => s.Name == "Master Task & KSA List");

            if (rolesSheet == null || tasksSheet == null)
                throw new ArgumentException("DCWF XLSX must have 'DCWF Roles' and 'Master Task & KSA List' sheets.");

            var frameworkEntity = new CompetencyFrameworkEntity
            {
                Id = Guid.NewGuid(),
                Name = $"{source} {version}",
                IdNumber = source,
                Description = $"Imported from DCWF {version}",
                Source = source,
                Version = version,
                CreatedBy = userId
            };

            var idNumberToEntity = new Dictionary<string, CompetencyEntity>();
            var idNumberToParent = new Dictionary<string, string>();
            var idNumberToRelated = new Dictionary<string, List<string>>();
            int sortOrder = 0;

            // Parse "DCWF Roles" sheet - categories and work roles
            var rolesSheetPart = (WorksheetPart)workbookPart.GetPartById(rolesSheet.Id);
            var rolesData = rolesSheetPart.Worksheet.Elements<SheetData>().First();
            var rolesRows = rolesData.Elements<Row>().ToList();

            // Skip first 2 rows (empty + header), start at row 3
            if (rolesRows.Count > 2)
            {
                for (int i = 2; i < rolesRows.Count; i++)
                {
                    var row = rolesRows[i];
                    var cells = row.Elements<Cell>().ToList();
                    var cellDict = new Dictionary<int, string>();
                    foreach (var cell in cells)
                    {
                        var colIndex = GetColumnIndex(cell.CellReference);
                        if (colIndex >= 0)
                        {
                            cellDict[colIndex] = GetCellValue(cell, workbookPart) ?? "";
                        }
                    }

                    // Column B (1) = Category name with code, Column D (3) = Work Role Name, Column E (4) = DCWF Code
                    var categoryText = cellDict.GetValueOrDefault(1, "").Trim();
                    var roleName = cellDict.GetValueOrDefault(3, "").Trim();
                    var roleCodeNum = cellDict.GetValueOrDefault(4, "").Trim();

                    // Parse category (format: "Category Name\n(CODE)")
                    if (!string.IsNullOrWhiteSpace(categoryText) && categoryText.Contains("(") && categoryText.Contains(")"))
                    {
                        var parts = categoryText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        var catName = parts.Length > 0 ? parts[0].Trim() : "";
                        var catCode = "";
                        if (parts.Length > 1)
                        {
                            var codePart = parts[1].Trim();
                            catCode = codePart.Trim('(', ')');
                        }

                        if (!string.IsNullOrWhiteSpace(catCode) && !idNumberToEntity.ContainsKey(catCode))
                        {
                            var catDesc = cellDict.GetValueOrDefault(2, "").Trim(); // Column C = category description
                            idNumberToEntity[catCode] = new CompetencyEntity
                            {
                                Id = Guid.NewGuid(),
                                CompetencyFrameworkId = frameworkEntity.Id,
                                IdNumber = catCode,
                                ShortName = catName,
                                Description = string.IsNullOrWhiteSpace(catDesc) ? catName : catDesc,
                                SortOrder = sortOrder++,
                                CreatedBy = userId
                            };
                        }
                    }

                    // Parse work role - need to combine category prefix with code number
                    if (!string.IsNullOrWhiteSpace(roleName) && !string.IsNullOrWhiteSpace(roleCodeNum))
                    {
                        // Extract category code from categoryText to build full role code like "IT-411"
                        string catPrefix = "";
                        if (!string.IsNullOrWhiteSpace(categoryText) && categoryText.Contains("(") && categoryText.Contains(")"))
                        {
                            var parts = categoryText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1)
                            {
                                var codePart = parts[1].Trim();
                                catPrefix = codePart.Trim('(', ')');
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(catPrefix))
                        {
                            var roleCode = $"{catPrefix}-{roleCodeNum}";
                            idNumberToEntity[roleCode] = new CompetencyEntity
                            {
                                Id = Guid.NewGuid(),
                                CompetencyFrameworkId = frameworkEntity.Id,
                                IdNumber = roleCode,
                                ShortName = roleName,
                                Description = roleName,
                                SortOrder = sortOrder++,
                                CreatedBy = userId
                            };

                            // Work role parent is the category
                            idNumberToParent[roleCode] = catPrefix;
                        }
                    }
                }
            }

            // Parse "Master Task & KSA List" sheet - tasks, knowledge, skills, abilities
            var tasksSheetPart = (WorksheetPart)workbookPart.GetPartById(tasksSheet.Id);
            var tasksData = tasksSheetPart.Worksheet.Elements<SheetData>().First();
            var tasksRows = tasksData.Elements<Row>().ToList();

            // Skip header row (row 1), start at row 2
            if (tasksRows.Count > 1)
            {
                for (int i = 1; i < tasksRows.Count; i++)
                {
                    var row = tasksRows[i];
                    var cells = row.Elements<Cell>().ToList();
                    var cellDict = new Dictionary<int, string>();
                    foreach (var cell in cells)
                    {
                        var colIndex = GetColumnIndex(cell.CellReference);
                        if (colIndex >= 0)
                        {
                            cellDict[colIndex] = GetCellValue(cell, workbookPart) ?? "";
                        }
                    }

                    // Column A (0) = DCWF Number (like "390A"), Column D (3) = Type, Column E (4) = Description
                    var dcwfNumber = cellDict.GetValueOrDefault(0, "").Trim();
                    var type = cellDict.GetValueOrDefault(3, "").Trim();
                    var description = cellDict.GetValueOrDefault(4, "").Trim();

                    if (string.IsNullOrWhiteSpace(dcwfNumber) || string.IsNullOrWhiteSpace(description))
                        continue;

                    // Convert type to ID prefix (Task->T-, Knowledge->K-, Skill->S-, Ability->A-)
                    var prefix = type.ToUpper() switch
                    {
                        "TASK" => "T-",
                        "KNOWLEDGE" => "K-",
                        "SKILL" => "S-",
                        "ABILITY" => "A-",
                        _ => ""
                    };

                    if (string.IsNullOrEmpty(prefix))
                        continue;

                    var idNumber = $"{prefix}{dcwfNumber}";

                    // Skip duplicates (variants like 390 and 390A both exist)
                    if (idNumberToEntity.ContainsKey(idNumber))
                        continue;

                    idNumberToEntity[idNumber] = new CompetencyEntity
                    {
                        Id = Guid.NewGuid(),
                        CompetencyFrameworkId = frameworkEntity.Id,
                        IdNumber = idNumber,
                        ShortName = description.Length > 100 ? description.Substring(0, 100) + "..." : description,
                        Description = description,
                        SortOrder = sortOrder++,
                        CreatedBy = userId
                    };
                }
            }

            // Parse per-role sheets for relationships (format: "(CODE) Role Name")
            foreach (var sheet in sheets)
            {
                var sheetName = sheet.Name.Value;
                if (!sheetName.StartsWith("(") || !sheetName.Contains(")"))
                    continue;

                // Extract role code from sheet name like "(IT-411) Tech Supp Specialist"
                var endParen = sheetName.IndexOf(')');
                var roleCode = sheetName.Substring(1, endParen - 1).Trim();

                if (!idNumberToEntity.ContainsKey(roleCode))
                    continue;

                var roleSheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id);
                var roleData = roleSheetPart.Worksheet.Elements<SheetData>().First();
                var roleRows = roleData.Elements<Row>().ToList();

                // Skip first 6 rows (header info), start at row 7
                if (roleRows.Count > 6)
                {
                    for (int i = 6; i < roleRows.Count; i++)
                    {
                        var row = roleRows[i];
                        var cells = row.Elements<Cell>().ToList();
                        var cellDict = new Dictionary<int, string>();
                        foreach (var cell in cells)
                        {
                            var colIndex = GetColumnIndex(cell.CellReference);
                            if (colIndex >= 0)
                            {
                                cellDict[colIndex] = GetCellValue(cell, workbookPart) ?? "";
                            }
                        }

                        // Column A (0) = DCWF Number, Column B (1) = Type
                        var dcwfNumber = cellDict.GetValueOrDefault(0, "").Trim();
                        var type = cellDict.GetValueOrDefault(1, "").Trim();

                        if (string.IsNullOrWhiteSpace(dcwfNumber))
                            continue;

                        var prefix = type.ToUpper() switch
                        {
                            "TASK" => "T-",
                            "KNOWLEDGE" => "K-",
                            "SKILL" => "S-",
                            "ABILITY" => "A-",
                            _ => ""
                        };

                        if (string.IsNullOrEmpty(prefix))
                            continue;

                        var tksaId = $"{prefix}{dcwfNumber}";

                        // Create relationship: role -> TKSA
                        if (!idNumberToRelated.ContainsKey(roleCode))
                            idNumberToRelated[roleCode] = new List<string>();

                        // Only add if the TKSA exists in our dictionary
                        if (idNumberToEntity.ContainsKey(tksaId))
                        {
                            idNumberToRelated[roleCode].Add(tksaId);
                        }
                    }
                }
            }

            if (idNumberToEntity.Count == 0)
                throw new ArgumentException("No competencies found in DCWF spreadsheet.");

            using var transaction = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                // 1. Save framework
                _context.CompetencyFrameworks.Add(frameworkEntity);
                await _context.SaveChangesAsync(ct);

                // 2. Batch-insert competencies (without parents)
                foreach (var batch in idNumberToEntity.Values.Chunk(BatchSize))
                {
                    _context.Competencies.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                }

                // 3. Set parent relationships and build paths
                foreach (var (childId, parentId) in idNumberToParent)
                {
                    if (idNumberToEntity.TryGetValue(childId, out var child) &&
                        idNumberToEntity.TryGetValue(parentId, out var parent))
                    {
                        child.ParentId = parent.Id;
                    }
                }
                var byId = idNumberToEntity.Values.ToDictionary(e => e.Id);
                foreach (var entity in idNumberToEntity.Values)
                {
                    entity.Path = BuildPath(entity, byId);
                }
                await _context.SaveChangesAsync(ct);

                // 4. Batch-insert relationships
                var relationshipEntities = new List<CompetencyRelationshipEntity>();
                foreach (var (sourceId, relatedIds) in idNumberToRelated)
                {
                    if (!idNumberToEntity.TryGetValue(sourceId, out var src))
                        continue;

                    foreach (var destId in relatedIds)
                    {
                        if (idNumberToEntity.TryGetValue(destId, out var dst))
                        {
                            relationshipEntities.Add(new CompetencyRelationshipEntity
                            {
                                Id = Guid.NewGuid(),
                                CompetencyId = src.Id,
                                RelatedCompetencyId = dst.Id,
                                CreatedBy = userId
                            });
                        }
                    }
                }

                foreach (var batch in relationshipEntities.Chunk(BatchSize))
                {
                    _context.CompetencyRelationships.AddRange(batch);
                    await _context.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                throw new ArgumentException(
                    $"Database error importing framework: {ex.InnerException?.Message ?? ex.Message}");
            }

            return await GetAsync(frameworkEntity.Id, ct);
        }

        public async Task<ViewModels.CompetencyFrameworkImportPreview> PreviewCsvAsync(Stream csvStream, string source, string version, CancellationToken ct)
        {
            var preview = new ViewModels.CompetencyFrameworkImportPreview
            {
                Source = source,
                Version = version
            };

            try
            {
                using var reader = new StreamReader(csvStream);
                var lines = new List<string>();
                string line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(line);
                }

                if (lines.Count < 2)
                {
                    preview.Error = "CSV file must have a header row and at least one data row.";
                    return preview;
                }

                // Skip HTML junk if present (Moodle exports sometimes prepend HTML/metadata)
                // Look for the Moodle CSV header that contains "Parent ID number"
                int headerIndex = -1;
                int dataStartIndex = 0;

                for (int i = 0; i < lines.Count; i++)
                {
                    var currentLine = lines[i];
                    if (currentLine.Contains("Parent ID number", StringComparison.OrdinalIgnoreCase))
                    {
                        headerIndex = i;
                        dataStartIndex = i + 1;
                        // If HTML junk precedes the header on the same line, strip everything before it
                        var idx = currentLine.IndexOf("Parent ID number", StringComparison.OrdinalIgnoreCase);
                        if (idx > 0)
                        {
                            lines[i] = currentLine.Substring(idx);
                        }
                        break;
                    }
                }

                if (headerIndex < 0 || dataStartIndex >= lines.Count)
                {
                    preview.Error = "CSV file must contain a Moodle lpimportcsv header row with 'Parent ID number'.";
                    return preview;
                }

                // Parse header
                var headerCols = lines[headerIndex].Split(',');
                if (headerCols.Length < 14)
                {
                    preview.Error = "CSV file must have 14 columns (Moodle lpimportcsv format).";
                    return preview;
                }

                preview.FrameworkName = $"{source} {version}";

                // Count competencies and relationships
                var typeCounts = new Dictionary<string, int>();
                int relationshipCount = 0;

                for (int i = headerIndex + 1; i < lines.Count; i++)
                {
                    var cols = lines[i].Split(',');
                    if (cols.Length < 2) continue;

                    var shortname = cols[0].Trim('"');
                    if (string.IsNullOrWhiteSpace(shortname)) continue;

                    // Infer type from ID pattern or hierarchy
                    var type = "competency";
                    if (shortname.StartsWith("WRL-")) type = "work_role";
                    else if (shortname.StartsWith("T-")) type = "task";
                    else if (shortname.StartsWith("K-")) type = "knowledge";
                    else if (shortname.StartsWith("S-")) type = "skill";
                    else if (shortname.StartsWith("A-")) type = "ability";

                    typeCounts[type] = typeCounts.GetValueOrDefault(type, 0) + 1;

                    // Count relationships (column 12 = relatedidnumbers)
                    if (cols.Length > 11 && !string.IsNullOrWhiteSpace(cols[11]))
                    {
                        var related = cols[11].Trim('"').Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                        relationshipCount += related.Length;
                    }
                }

                preview.ElementTypeCounts = typeCounts.Select(kvp => new ViewModels.ElementTypeCount
                {
                    Type = kvp.Key,
                    Count = kvp.Value
                }).ToList();

                preview.TotalElements = typeCounts.Values.Sum();
                preview.TotalRelationships = relationshipCount;
            }
            catch (Exception ex)
            {
                preview.Error = $"Failed to parse CSV: {ex.Message}";
            }

            return preview;
        }

        public async Task<ViewModels.CompetencyFrameworkImportPreview> PreviewJsonAsync(Stream jsonStream, CancellationToken ct)
        {
            var preview = new ViewModels.CompetencyFrameworkImportPreview();

            try
            {
                var jsonDoc = await JsonSerializer.DeserializeAsync<JsonElement>(jsonStream, cancellationToken: ct);

                // Navigate to the container
                var root = jsonDoc;
                if (root.TryGetProperty("response", out var response))
                    root = response;
                if (root.TryGetProperty("elements", out var elementsCandidate) &&
                    elementsCandidate.ValueKind == JsonValueKind.Object)
                    root = elementsCandidate;

                // Parse document metadata
                if (root.TryGetProperty("documents", out var documents))
                {
                    var doc = documents.EnumerateArray().FirstOrDefault();
                    preview.FrameworkName = doc.TryGetProperty("name", out var n) ? n.GetString() : "Imported Framework";
                    preview.Version = doc.TryGetProperty("version", out var v) ? v.GetString() : "";
                    preview.Source = doc.TryGetProperty("doc_identifier", out var d) ? d.GetString() : "";
                }

                // Parse elements
                if (root.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
                {
                    var typeCounts = new Dictionary<string, int>();
                    var skippedTypes = new HashSet<string> { "sort", "opm_code" };

                    foreach (var el in elements.EnumerateArray())
                    {
                        if (el.TryGetProperty("element_type", out var typeEl))
                        {
                            var elementType = typeEl.GetString();
                            if (skippedTypes.Contains(elementType)) continue;
                            typeCounts[elementType] = typeCounts.GetValueOrDefault(elementType, 0) + 1;
                        }
                    }

                    preview.ElementTypeCounts = typeCounts.Select(kvp => new ViewModels.ElementTypeCount
                    {
                        Type = kvp.Key,
                        Count = kvp.Value
                    }).ToList();

                    preview.TotalElements = typeCounts.Values.Sum();
                }

                // Parse relationships
                if (root.TryGetProperty("relationships", out var relationships) && relationships.ValueKind == JsonValueKind.Array)
                {
                    preview.TotalRelationships = relationships.GetArrayLength();
                }
            }
            catch (Exception ex)
            {
                preview.Error = $"Failed to parse JSON: {ex.Message}";
            }

            return preview;
        }

        public async Task<ViewModels.CompetencyFrameworkImportPreview> PreviewXlsxAsync(Stream xlsxStream, string source, string version, CancellationToken ct)
        {
            var preview = new ViewModels.CompetencyFrameworkImportPreview
            {
                Source = source,
                Version = version,
                FrameworkName = $"{source} {version}"
            };

            try
            {
                using var document = SpreadsheetDocument.Open(xlsxStream, false);
                var workbookPart = document.WorkbookPart;
                var sheets = workbookPart.Workbook.Sheets.Elements<Sheet>().ToList();

                // Check if this is a DCWF multi-sheet file
                var rolesSheet = sheets.FirstOrDefault(s => s.Name == "DCWF Roles");
                var tasksSheet = sheets.FirstOrDefault(s => s.Name == "Master Task & KSA List");

                if (rolesSheet != null && tasksSheet != null)
                {
                    // DCWF multi-sheet format
                    var typeCounts = new Dictionary<string, int>();
                    int relationshipCount = 0;

                    // Count categories and work roles from "DCWF Roles"
                    var rolesSheetPart = (WorksheetPart)workbookPart.GetPartById(rolesSheet.Id);
                    var rolesData = rolesSheetPart.Worksheet.Elements<SheetData>().First();
                    var rolesRows = rolesData.Elements<Row>().Skip(2).ToList(); // Skip first 2 rows (empty + header)

                    var categories = new HashSet<string>();
                    foreach (var row in rolesRows)
                    {
                        var cells = row.Elements<Cell>().ToList();
                        var cellDict = new Dictionary<int, string>();
                        foreach (var cell in cells)
                        {
                            var colIndex = GetColumnIndex(cell.CellReference);
                            if (colIndex >= 0)
                            {
                                cellDict[colIndex] = GetCellValue(cell, workbookPart) ?? "";
                            }
                        }

                        var categoryText = cellDict.GetValueOrDefault(1, "").Trim(); // Column B
                        var roleName = cellDict.GetValueOrDefault(3, "").Trim(); // Column D
                        var roleCodeNum = cellDict.GetValueOrDefault(4, "").Trim(); // Column E

                        // Extract category code
                        if (!string.IsNullOrWhiteSpace(categoryText) && categoryText.Contains("(") && categoryText.Contains(")"))
                        {
                            var parts = categoryText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1)
                            {
                                var codePart = parts[1].Trim();
                                var catCode = codePart.Trim('(', ')');
                                categories.Add(catCode);
                            }
                        }

                        // Count work role
                        if (!string.IsNullOrWhiteSpace(roleName) && !string.IsNullOrWhiteSpace(roleCodeNum))
                        {
                            typeCounts["work_role"] = typeCounts.GetValueOrDefault("work_role", 0) + 1;
                        }
                    }
                    typeCounts["category"] = categories.Count;

                    // Count TKSAs from "Master Task & KSA List"
                    var tasksSheetPart = (WorksheetPart)workbookPart.GetPartById(tasksSheet.Id);
                    var tasksData = tasksSheetPart.Worksheet.Elements<SheetData>().First();
                    var tasksRows = tasksData.Elements<Row>().Skip(1).ToList(); // Skip header

                    var seenIds = new HashSet<string>();
                    foreach (var row in tasksRows)
                    {
                        var cells = row.Elements<Cell>().ToList();
                        var cellDict = new Dictionary<int, string>();
                        foreach (var cell in cells)
                        {
                            var colIndex = GetColumnIndex(cell.CellReference);
                            if (colIndex >= 0)
                            {
                                cellDict[colIndex] = GetCellValue(cell, workbookPart) ?? "";
                            }
                        }

                        var dcwfNumber = cellDict.GetValueOrDefault(0, "").Trim(); // Column A
                        var type = cellDict.GetValueOrDefault(3, "").Trim().ToLower(); // Column D

                        if (string.IsNullOrWhiteSpace(dcwfNumber))
                            continue;

                        // Build ID with prefix
                        var prefix = type switch
                        {
                            "task" => "T-",
                            "knowledge" => "K-",
                            "skill" => "S-",
                            "ability" => "A-",
                            _ => ""
                        };

                        if (string.IsNullOrEmpty(prefix))
                            continue;

                        var id = $"{prefix}{dcwfNumber}";
                        if (seenIds.Contains(id))
                            continue;
                        seenIds.Add(id);

                        if (type == "task")
                            typeCounts["task"] = typeCounts.GetValueOrDefault("task", 0) + 1;
                        else if (type == "knowledge")
                            typeCounts["knowledge"] = typeCounts.GetValueOrDefault("knowledge", 0) + 1;
                        else if (type == "skill")
                            typeCounts["skill"] = typeCounts.GetValueOrDefault("skill", 0) + 1;
                        else if (type == "ability")
                            typeCounts["ability"] = typeCounts.GetValueOrDefault("ability", 0) + 1;
                    }

                    // Count relationships from per-role sheets
                    foreach (var sheet in sheets)
                    {
                        var sheetName = sheet.Name.Value;
                        if (!sheetName.StartsWith("(") || !sheetName.Contains(")"))
                            continue;

                        var roleSheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id);
                        var roleData = roleSheetPart.Worksheet.Elements<SheetData>().First();
                        var roleRows = roleData.Elements<Row>().Skip(6).ToList(); // Skip first 6 rows (header info)

                        relationshipCount += roleRows.Count(r =>
                        {
                            var cells = r.Elements<Cell>().ToList();
                            if (cells.Count == 0) return false;
                            var firstCell = cells.FirstOrDefault();
                            if (firstCell == null) return false;
                            var value = GetCellValue(firstCell, workbookPart);
                            return !string.IsNullOrWhiteSpace(value);
                        });
                    }

                    preview.ElementTypeCounts = typeCounts.Select(kvp => new ViewModels.ElementTypeCount
                    {
                        Type = kvp.Key,
                        Count = kvp.Value
                    }).ToList();

                    preview.TotalElements = typeCounts.Values.Sum();
                    preview.TotalRelationships = relationshipCount;
                }
                else
                {
                    // Simple single-sheet format fallback
                    var worksheetPart = workbookPart.WorksheetParts.First();
                    var sheetData = worksheetPart.Worksheet.Elements<SheetData>().First();
                    var rows = sheetData.Elements<Row>().ToList();

                    if (rows.Count < 2)
                    {
                        preview.Error = "Spreadsheet must have a header row and at least one data row.";
                        return preview;
                    }

                    var typeCounts = new Dictionary<string, int>();
                    int relationshipCount = 0;

                    for (int i = 1; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        var cells = row.Elements<Cell>().ToList();

                        var cellDict = new Dictionary<int, string>();
                        foreach (var cell in cells)
                        {
                            var colIndex = GetColumnIndex(cell.CellReference);
                            if (colIndex >= 0)
                            {
                                cellDict[colIndex] = GetCellValue(cell, workbookPart) ?? "";
                            }
                        }

                        if (!cellDict.ContainsKey(0)) continue;

                        var idNumber = cellDict.GetValueOrDefault(0, "").Trim();
                        if (string.IsNullOrWhiteSpace(idNumber)) continue;

                        var type = "competency";
                        if (idNumber.StartsWith("WRL-")) type = "work_role";
                        else if (idNumber.StartsWith("T-")) type = "task";
                        else if (idNumber.StartsWith("K-")) type = "knowledge";
                        else if (idNumber.StartsWith("S-")) type = "skill";
                        else if (idNumber.StartsWith("A-")) type = "ability";
                        else if (idNumber.Length <= 3 && !idNumber.Contains("-")) type = "category";

                        typeCounts[type] = typeCounts.GetValueOrDefault(type, 0) + 1;

                        if (cellDict.ContainsKey(4))
                        {
                            var relatedIds = cellDict[4].Trim();
                            if (!string.IsNullOrWhiteSpace(relatedIds))
                            {
                                var related = relatedIds.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                                relationshipCount += related.Length;
                            }
                        }
                    }

                    preview.ElementTypeCounts = typeCounts.Select(kvp => new ViewModels.ElementTypeCount
                    {
                        Type = kvp.Key,
                        Count = kvp.Value
                    }).ToList();

                    preview.TotalElements = typeCounts.Values.Sum();
                    preview.TotalRelationships = relationshipCount;
                }
            }
            catch (Exception ex)
            {
                preview.Error = $"Failed to parse XLSX: {ex.Message}";
            }

            return preview;
        }

        private string GetCellValue(Cell cell, WorkbookPart workbookPart)
        {
            if (cell == null) return null;

            var value = cell.CellValue?.Text;
            if (string.IsNullOrEmpty(value)) return null;

            if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString)
            {
                var stringTable = workbookPart.SharedStringTablePart.SharedStringTable;
                return stringTable.ElementAt(int.Parse(value)).InnerText;
            }

            return value;
        }

        private int GetColumnIndex(string cellReference)
        {
            if (string.IsNullOrEmpty(cellReference)) return -1;

            var column = new string(cellReference.Where(char.IsLetter).ToArray());
            int index = 0;
            foreach (char c in column)
            {
                index = index * 26 + (c - 'A' + 1);
            }
            return index - 1;
        }

        private int FindColumn(List<string> headers, string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var index = headers.IndexOf(candidate);
                if (index >= 0) return index;
            }
            return -1;
        }

        public async Task<ViewModels.Competency> CreateCompetencyAsync(Guid frameworkId, ViewModels.Competency competency, CancellationToken ct)
        {
            var framework = await _context.CompetencyFrameworks
                .SingleOrDefaultAsync(f => f.Id == frameworkId, ct);
            if (framework == null)
                throw new EntityNotFoundException<ViewModels.CompetencyFramework>();

            var userId = _user.GetId();
            var entity = _mapper.Map<CompetencyEntity>(competency);
            entity.Id = Guid.NewGuid();
            entity.CompetencyFrameworkId = frameworkId;
            entity.CreatedBy = userId;

            _context.Competencies.Add(entity);
            await _context.SaveChangesAsync(ct);

            // Create relationships from relatedIdNumbers
            if (competency.RelatedIdNumbers != null && competency.RelatedIdNumbers.Count > 0)
            {
                var relatedIdNumbers = competency.RelatedIdNumbers
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToList();

                if (relatedIdNumbers.Count > 0)
                {
                    var idNumberToGuid = await _context.Competencies
                        .Where(c => c.CompetencyFrameworkId == frameworkId
                                    && relatedIdNumbers.Contains(c.IdNumber))
                        .ToDictionaryAsync(c => c.IdNumber, c => c.Id, ct);

                    foreach (var relatedId in relatedIdNumbers)
                    {
                        if (idNumberToGuid.TryGetValue(relatedId, out var destGuid))
                        {
                            _context.CompetencyRelationships.Add(new CompetencyRelationshipEntity
                            {
                                Id = Guid.NewGuid(),
                                CompetencyId = entity.Id,
                                RelatedCompetencyId = destGuid,
                                CreatedBy = userId
                            });
                        }
                    }
                    await _context.SaveChangesAsync(ct);
                }
            }

            return _mapper.Map<ViewModels.Competency>(entity);
        }

        public async Task<ViewModels.Competency> UpdateCompetencyAsync(Guid competencyId, ViewModels.Competency competency, CancellationToken ct)
        {
            var entity = await _context.Competencies
                .Include(c => c.Relationships)
                .SingleOrDefaultAsync(c => c.Id == competencyId, ct);
            if (entity == null)
                throw new EntityNotFoundException<ViewModels.Competency>();

            var userId = _user.GetId();
            entity.IdNumber = competency.IdNumber;
            entity.ShortName = competency.ShortName;
            entity.Description = competency.Description;
            entity.ParentId = competency.ParentId;
            entity.SortOrder = competency.SortOrder;
            entity.ModifiedBy = userId;

            // Update relationships if relatedIdNumbers provided
            if (competency.RelatedIdNumbers != null)
            {
                // Remove existing outbound relationships
                _context.CompetencyRelationships.RemoveRange(entity.Relationships);

                // Resolve new relatedIdNumbers to entity IDs
                var relatedIdNumbers = competency.RelatedIdNumbers
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToList();

                if (relatedIdNumbers.Count > 0)
                {
                    var idNumberToGuid = await _context.Competencies
                        .Where(c => c.CompetencyFrameworkId == entity.CompetencyFrameworkId
                                    && relatedIdNumbers.Contains(c.IdNumber))
                        .ToDictionaryAsync(c => c.IdNumber, c => c.Id, ct);

                    foreach (var relatedId in relatedIdNumbers)
                    {
                        if (idNumberToGuid.TryGetValue(relatedId, out var destGuid))
                        {
                            _context.CompetencyRelationships.Add(new CompetencyRelationshipEntity
                            {
                                Id = Guid.NewGuid(),
                                CompetencyId = entity.Id,
                                RelatedCompetencyId = destGuid,
                                CreatedBy = userId
                            });
                        }
                    }
                }
            }

            await _context.SaveChangesAsync(ct);
            return _mapper.Map<ViewModels.Competency>(entity);
        }

        public async Task<bool> DeleteCompetencyAsync(Guid competencyId, CancellationToken ct)
        {
            var entity = await _context.Competencies
                .SingleOrDefaultAsync(c => c.Id == competencyId, ct);
            if (entity == null)
                throw new EntityNotFoundException<ViewModels.Competency>();

            _context.Competencies.Remove(entity);
            await _context.SaveChangesAsync(ct);
            return true;
        }

        public async Task<ViewModels.FrameworkDeleteCheck> CheckCanDeleteAsync(Guid id, CancellationToken ct)
        {
            var framework = await _context.CompetencyFrameworks
                .AsNoTracking()
                .SingleOrDefaultAsync(f => f.Id == id, ct);

            if (framework == null)
                throw new EntityNotFoundException<ViewModels.CompetencyFramework>();

            var competencyIds = await _context.Competencies
                .Where(c => c.CompetencyFrameworkId == id)
                .Select(c => c.Id)
                .ToListAsync(ct);

            if (competencyIds.Count == 0)
            {
                return new ViewModels.FrameworkDeleteCheck
                {
                    CanDelete = true,
                    AffectedMsels = new List<ViewModels.MselReference>()
                };
            }

            // Check MselCompetency references (MSEL pool)
            var affectedMsels = await _context.MselCompetencies
                .Where(mc => competencyIds.Contains(mc.CompetencyId))
                .Select(mc => new { mc.Msel.Id, mc.Msel.Name })
                .Distinct()
                .ToListAsync(ct);

            return new ViewModels.FrameworkDeleteCheck
            {
                CanDelete = affectedMsels.Count == 0,
                AffectedMsels = affectedMsels.Select(m => new ViewModels.MselReference { Id = m.Id, Name = m.Name }).ToList()
            };
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
        {
            var check = await CheckCanDeleteAsync(id, ct);

            if (!check.CanDelete)
            {
                var message = $"Cannot delete framework. It is being used by {check.AffectedMsels.Count} MSEL(s).\n\n";
                message += "Remove competencies from these MSELs before deleting the framework.";

                throw new ArgumentException(message);
            }

            var framework = await _context.CompetencyFrameworks
                .SingleOrDefaultAsync(f => f.Id == id, ct);

            if (framework == null)
                throw new EntityNotFoundException<ViewModels.CompetencyFramework>();

            _context.CompetencyFrameworks.Remove(framework);
            await _context.SaveChangesAsync(ct);

            return true;
        }

        private string BuildPath(CompetencyEntity entity, Dictionary<Guid, CompetencyEntity> byId)
        {
            var parts = new List<string>();
            var current = entity;
            while (current != null)
            {
                parts.Insert(0, current.Id.ToString());
                current = current.ParentId.HasValue && byId.ContainsKey(current.ParentId.Value)
                    ? byId[current.ParentId.Value]
                    : null;
            }
            return "/" + string.Join("/", parts);
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, out var result) ? result : 0;
        }

        /// <summary>
        /// If shortName starts with "idNumber - " or "idNumber- ", strips the prefix
        /// so the name column shows the human-readable name without redundant ID.
        /// </summary>
        private static string StripIdPrefix(string shortName, string idNumber)
        {
            if (string.IsNullOrWhiteSpace(shortName) || string.IsNullOrWhiteSpace(idNumber))
                return shortName;
            var prefix = idNumber + " - ";
            if (shortName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return shortName.Substring(prefix.Length).TrimStart();
            prefix = idNumber + "- ";
            if (shortName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return shortName.Substring(prefix.Length).TrimStart();
            return shortName;
        }

        #region Moodle CSV Parsing

        private class MoodleCsvRow
        {
            public string ParentIdNumber { get; set; } = "";
            public string IdNumber { get; set; } = "";
            public string ShortName { get; set; } = "";
            public string Description { get; set; } = "";
            public int DescriptionFormat { get; set; }
            public string ScaleValues { get; set; } = "";
            public string ScaleConfiguration { get; set; } = "";
            public string RuleType { get; set; } = "";
            public string RuleOutcome { get; set; } = "";
            public string RuleConfig { get; set; } = "";
            public string RelatedIdNumbers { get; set; } = "";
            public string ExportId { get; set; } = "";
            public bool IsFramework { get; set; }
            public string Taxonomy { get; set; } = "";
        }

        /// <summary>
        /// Parses Moodle's 14-column lpimportcsv format.
        /// Column order: Parent ID Number, ID Number, Short Name, Description,
        /// Description Format, Scale Values, Scale Configuration, Rule Type,
        /// Rule Outcome, Rule Config, Cross Referenced Competency ID Numbers,
        /// Export ID, Is Framework, Taxonomy
        /// </summary>
        private List<MoodleCsvRow> ParseMoodleCsv(Stream stream)
        {
            var rows = new List<MoodleCsvRow>();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Skip lines until we find the actual CSV header containing "Parent ID number"
            // Moodle exports sometimes prepend HTML/metadata that may even be on the same line as the header
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("Parent ID number", StringComparison.OrdinalIgnoreCase))
                {
                    // If HTML junk precedes the header on the same line, strip everything before it
                    var idx = line.IndexOf("Parent ID number", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                        line = line.Substring(idx);
                    break;
                }
            }
            if (line == null) return rows;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var fields = ParseCsvLine(line);
                if (fields.Count < 14) continue;

                rows.Add(new MoodleCsvRow
                {
                    ParentIdNumber = fields[0].Trim(),
                    IdNumber = fields[1].Trim(),
                    ShortName = fields[2].Trim(),
                    Description = fields[3].Trim(),
                    DescriptionFormat = ParseInt(fields[4].Trim()),
                    ScaleValues = fields[5].Trim(),
                    ScaleConfiguration = fields[6].Trim(),
                    RuleType = fields[7].Trim(),
                    RuleOutcome = fields[8].Trim(),
                    RuleConfig = fields[9].Trim(),
                    RelatedIdNumbers = fields[10].Trim(),
                    ExportId = fields[11].Trim(),
                    IsFramework = fields[12].Trim() == "1",
                    Taxonomy = fields[13].Trim()
                });
            }

            return rows;
        }

        /// <summary>
        /// Parses a single CSV line handling RFC 4180 quoted fields.
        /// </summary>
        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        fields.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            fields.Add(current.ToString());
            return fields;
        }

        #endregion
    }
}
