// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
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
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface IDataOptionService
    {
        Task<IEnumerable<ViewModels.DataOption>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<ViewModels.DataOption>> GetByDataFieldAsync(Guid dataFieldId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.DataOption> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.DataOption> CreateAsync(ViewModels.DataOption dataOption, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<ViewModels.DataOption> UpdateAsync(Guid id, ViewModels.DataOption dataOption, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
        Task<ViewModels.DataOptionImportPreview> PreviewImportAsync(Guid dataFieldId, IFormFile file, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct);
    }

    public class DataOptionService : IDataOptionService
    {
        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public DataOptionService(
            BlueprintContext context,
            IPrincipal user,
            IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
        }

        public async Task<IEnumerable<ViewModels.DataOption>> GetByMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            if (!hasSystemPermission && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            var dataFieldIdList = await _context.DataFields
                .Where(df => df.MselId == mselId)
                .Select(df => df.Id)
                .ToListAsync(ct);
            var dataOptionEntities = await _context.DataOptions
                .Where(op => dataFieldIdList.Contains(op.DataFieldId))
                .ToListAsync();

            return _mapper.Map<IEnumerable<DataOption>>(dataOptionEntities).ToList();;
        }

        public async Task<IEnumerable<ViewModels.DataOption>> GetByDataFieldAsync(Guid dataFieldId, bool hasSystemPermission, CancellationToken ct)
        {
            var dataField = await _context.DataFields.SingleOrDefaultAsync(df => df.Id == dataFieldId, ct);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>(dataFieldId.ToString());

            if (!(dataField.MselId == null) &&
                !hasSystemPermission &&
                !(await MselViewRequirement.IsMet(_user.GetId(), dataField.MselId, _context)))
                throw new ForbiddenException();

            var dataOptionEntities = await _context.DataOptions
                .Where(dataOption => dataOption.DataFieldId == dataFieldId)
                .ToListAsync();

            return _mapper.Map<IEnumerable<DataOption>>(dataOptionEntities).ToList();;
        }

        public async Task<ViewModels.DataOption> GetAsync(Guid id, bool hasSystemPermission, CancellationToken ct)
        {
            var dataOption = await _context.DataOptions.SingleOrDefaultAsync(dopt => dopt.Id == id, ct);
            if (dataOption == null)
                throw new EntityNotFoundException<DataOption>(id.ToString());

            var dataField = await _context.DataFields.SingleOrDefaultAsync(df => df.Id == dataOption.DataFieldId, ct);
            // Templates (null MselId) can be viewed by anyone
            if (dataField.MselId.HasValue)
            {
                if (!hasSystemPermission && !await MselViewRequirement.IsMet(_user.GetId(), dataField.MselId, _context))
                    throw new ForbiddenException();
            }

            return _mapper.Map<DataOption>(dataOption);
        }

        public async Task<ViewModels.DataOption> CreateAsync(ViewModels.DataOption dataOption, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataField = await _context.DataFields
                .FindAsync(dataOption.DataFieldId);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>("DataField not found when creating a DataOption.  " + dataOption.DataFieldId.ToString());

            if (dataField.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), dataField.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();
            }

            dataOption.Id = dataOption.Id != Guid.Empty ? dataOption.Id : Guid.NewGuid();
            dataOption.CreatedBy = _user.GetId();
            var dataOptionEntity = _mapper.Map<DataOptionEntity>(dataOption);
            _context.DataOptions.Add(dataOptionEntity);
            await _context.SaveChangesAsync(ct);
            // update the dataField
            var dataFieldEntity = await _context.DataFields.FindAsync(dataOption.DataFieldId);
            dataField.ModifiedBy = dataFieldEntity.CreatedBy;
            await _context.SaveChangesAsync(ct);
            dataOption = await GetAsync(dataOptionEntity.Id, true, ct);

            return dataOption;
        }

        public async Task<ViewModels.DataOption> UpdateAsync(Guid id, ViewModels.DataOption dataOption, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataField = await _context.DataFields
                .FindAsync(dataOption.DataFieldId);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>("DataField not found when updating a DataOption.  " + dataOption.DataFieldId.ToString());

            if (dataField.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), dataField.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();
            }

            var dataOptionToUpdate = await _context.DataOptions.SingleOrDefaultAsync(v => v.Id == id, ct);

            if (dataOptionToUpdate == null)
                throw new EntityNotFoundException<DataOption>();

            dataOption.ModifiedBy = _user.GetId();
            _mapper.Map(dataOption, dataOptionToUpdate);
            _context.DataOptions.Update(dataOptionToUpdate);
            await _context.SaveChangesAsync(ct);
            // updated the dataField
            var dataFieldEntity = await _context.DataFields.FindAsync(dataOption.DataFieldId);
            dataField.ModifiedBy = dataFieldEntity.ModifiedBy;
            await _context.SaveChangesAsync(ct);

            dataOption = await GetAsync(dataOptionToUpdate.Id, true, ct);

            return dataOption;
        }

        public async Task<bool> DeleteAsync(Guid id, bool hasMselPermission, bool hasDataFieldPermission, CancellationToken ct)
        {
            var dataOptionToDelete = await _context.DataOptions.SingleOrDefaultAsync(v => v.Id == id, ct);
            if (dataOptionToDelete == null)
                throw new EntityNotFoundException<DataOption>();

            var dataField = await _context.DataFields
                .SingleOrDefaultAsync(df => df.Id == dataOptionToDelete.DataFieldId, ct);
            if (dataField == null)
                throw new EntityNotFoundException<DataField>("DataField not found when deleting a DataOption.  " + dataOptionToDelete.DataFieldId.ToString());

            if (dataField.MselId.HasValue)
            {
                if (!hasMselPermission &&
                    !await MselOwnerRequirement.IsMet(_user.GetId(), dataField.MselId, _context) &&
                    !await MselEditorRequirement.IsMet(_user.GetId(), dataField.MselId, _context))
                    throw new ForbiddenException();
            }
            else
            {
                if (!hasDataFieldPermission)
                    throw new ForbiddenException();
            }

            _context.DataOptions.Remove(dataOptionToDelete);
            await _context.SaveChangesAsync(ct);
            // updated the dataField
            var dataFieldEntity = await _context.DataFields.FindAsync(dataOptionToDelete.DataFieldId);
            dataField.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);

            return true;
        }

        public async Task<ViewModels.DataOptionImportPreview> PreviewImportAsync(
            Guid dataFieldId,
            IFormFile file,
            bool hasMselPermission,
            bool hasDataFieldPermission,
            CancellationToken ct)
        {
            // Get the data field and check permissions
            var dataField = await _context.DataFields
                .Include(df => df.Msel)
                .SingleOrDefaultAsync(df => df.Id == dataFieldId, ct);

            if (dataField == null)
                throw new EntityNotFoundException<ViewModels.DataField>();

            var mselId = dataField.MselId ?? dataField.Msel?.Id;
            if (mselId.HasValue)
            {
                if (!hasMselPermission && !(await MselEditRequirement.IsMet(_user.GetId(), mselId.Value, _context)))
                    throw new ForbiddenException();
            }
            else if (!hasDataFieldPermission)
            {
                throw new ForbiddenException();
            }

            // Get existing options for duplicate detection
            var existingOptions = await _context.DataOptions
                .Where(o => o.DataFieldId == dataFieldId)
                .Select(o => o.OptionName.ToLower())
                .ToListAsync(ct);
            var existingSet = new HashSet<string>(existingOptions);

            var preview = new ViewModels.DataOptionImportPreview();

            try
            {
                var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant();

                if (ext == ".json")
                {
                    await ParseJsonAsync(file, preview, existingSet, ct);
                }
                else if (ext == ".csv")
                {
                    await ParseCsvAsync(file, preview, existingSet, ct);
                }
                else if (ext == ".xlsx" || ext == ".xls")
                {
                    await ParseXlsxAsync(file, preview, existingSet, ct);
                }
                else
                {
                    preview.Error = "Unsupported file type. Please use JSON, CSV, or XLSX.";
                }
            }
            catch (Exception ex)
            {
                preview.Error = $"Error parsing file: {ex.Message}";
            }

            return preview;
        }

        private async Task ParseJsonAsync(IFormFile file, ViewModels.DataOptionImportPreview preview, HashSet<string> existingSet, CancellationToken ct)
        {
            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(ct);
            var jsonDoc = JsonSerializer.Deserialize<JsonElement>(content);

            // Handle NICE Framework format
            if (jsonDoc.TryGetProperty("response", out var response) &&
                response.TryGetProperty("elements", out var elementsObj) &&
                elementsObj.TryGetProperty("elements", out var elements) &&
                elements.ValueKind == JsonValueKind.Array)
            {
                var skipTypes = new HashSet<string> { "sort", "opm_code" };
                foreach (var element in elements.EnumerateArray())
                {
                    if (element.TryGetProperty("element_type", out var typeEl) && skipTypes.Contains(typeEl.GetString()))
                        continue;

                    if (element.TryGetProperty("element_identifier", out var idEl))
                    {
                        var optionName = idEl.GetString();
                        var optionValue = element.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : "";
                        var optionDesc = element.TryGetProperty("text", out var textEl) ? textEl.GetString() : "";
                        AddPreviewItem(preview, optionName, optionValue, optionDesc, existingSet);
                    }
                }
            }
            // Handle flat NICE format
            else if (jsonDoc.TryGetProperty("elements", out var flatElements) && flatElements.ValueKind == JsonValueKind.Array)
            {
                var skipTypes = new HashSet<string> { "sort", "opm_code" };
                foreach (var element in flatElements.EnumerateArray())
                {
                    if (element.TryGetProperty("element_type", out var typeEl) && skipTypes.Contains(typeEl.GetString()))
                        continue;

                    if (element.TryGetProperty("element_identifier", out var idEl))
                    {
                        var optionName = idEl.GetString();
                        var optionValue = element.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : "";
                        var optionDesc = element.TryGetProperty("text", out var textEl) ? textEl.GetString() : "";
                        AddPreviewItem(preview, optionName, optionValue, optionDesc, existingSet);
                    }
                }
            }
            // Handle array of objects
            else if (jsonDoc.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in jsonDoc.EnumerateArray())
                {
                    var extracted = ExtractFields(item);
                    if (extracted.HasValue)
                        AddPreviewItem(preview, extracted.Value.optionName, extracted.Value.optionValue, extracted.Value.optionDesc, existingSet);
                }
            }
            // Handle object with array property
            else if (jsonDoc.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in jsonDoc.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            var extracted = ExtractFields(item);
                            if (extracted.HasValue)
                                AddPreviewItem(preview, extracted.Value.optionName, extracted.Value.optionValue, extracted.Value.optionDesc, existingSet);
                        }
                        break;
                    }
                }
            }

            if (preview.Items.Count == 0 && string.IsNullOrEmpty(preview.Error))
            {
                preview.Error = "No options found. Expected an array of objects with ID and name fields.";
            }
        }

        private (string optionName, string optionValue, string optionDesc)? ExtractFields(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            var optionName = TryGetProperty(element, "id", "identifier", "code", "optionName", "element_identifier");
            if (string.IsNullOrEmpty(optionName))
                return null;

            var optionValue = TryGetProperty(element, "name", "title", "optionValue", "value");
            var optionDesc = TryGetProperty(element, "description", "text", "optionDescription");

            return (optionName, optionValue ?? "", optionDesc ?? "");
        }

        private string TryGetProperty(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var prop))
                    return prop.GetString();
            }
            return null;
        }

        private async Task ParseCsvAsync(IFormFile file, ViewModels.DataOptionImportPreview preview, HashSet<string> existingSet, CancellationToken ct)
        {
            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
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
                return;
            }

            var headers = ParseCsvLine(lines[0]).Select(h => h.ToLower().Trim()).ToList();
            var idCol = FindColumn(headers, "id", "identifier", "code", "optionname");
            var nameCol = FindColumn(headers, "name", "title", "optionvalue");
            var descCol = FindColumn(headers, "description", "text", "optiondescription");

            for (int i = 1; i < lines.Count; i++)
            {
                var cols = ParseCsvLine(lines[i]);
                string optionName, optionValue, optionDesc;

                if (idCol >= 0)
                {
                    optionName = idCol < cols.Count ? cols[idCol].Trim() : "";
                    optionValue = nameCol >= 0 && nameCol < cols.Count ? cols[nameCol].Trim() : "";
                    optionDesc = descCol >= 0 && descCol < cols.Count ? cols[descCol].Trim() : "";
                }
                else
                {
                    // Positional fallback
                    optionName = cols.Count > 0 ? cols[0].Trim() : "";
                    optionValue = cols.Count > 1 ? cols[1].Trim() : "";
                    optionDesc = cols.Count > 2 ? cols[2].Trim() : "";
                }

                if (!string.IsNullOrEmpty(optionName))
                {
                    AddPreviewItem(preview, optionName, optionValue, optionDesc, existingSet);
                }
            }

            if (preview.Items.Count == 0 && string.IsNullOrEmpty(preview.Error))
            {
                preview.Error = "No valid rows found in CSV.";
            }
        }

        private List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else if (ch == '"')
                    {
                        inQuotes = false;
                    }
                    else
                    {
                        current.Append(ch);
                    }
                }
                else
                {
                    if (ch == '"')
                    {
                        inQuotes = true;
                    }
                    else if (ch == ',')
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(ch);
                    }
                }
            }
            result.Add(current.ToString());
            return result;
        }

        private int FindColumn(List<string> headers, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var index = headers.IndexOf(candidate);
                if (index >= 0) return index;
            }
            return -1;
        }

        private Task ParseXlsxAsync(IFormFile file, ViewModels.DataOptionImportPreview preview, HashSet<string> existingSet, CancellationToken ct)
        {
            // TODO: Implement XLSX parsing using EPPlus or similar
            // For now, return an error message
            preview.Error = "XLSX import is not yet implemented on the server. Please use JSON or CSV format.";
            return Task.CompletedTask;
        }

        private void AddPreviewItem(ViewModels.DataOptionImportPreview preview, string optionName, string optionValue, string optionDesc, HashSet<string> existingSet)
        {
            if (string.IsNullOrWhiteSpace(optionName))
                return;

            preview.Items.Add(new ViewModels.DataOptionImportPreviewItem
            {
                OptionName = optionName,
                OptionValue = optionValue ?? "",
                OptionDescription = optionDesc ?? "",
                Exists = existingSet.Contains(optionName.ToLower())
            });
        }

    }
}
