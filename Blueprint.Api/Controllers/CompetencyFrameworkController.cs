// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class CompetencyFrameworkController : BaseController
    {
        private readonly ICompetencyFrameworkService _competencyFrameworkService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public CompetencyFrameworkController(ICompetencyFrameworkService competencyFrameworkService, IBlueprintAuthorizationService authorizationService)
        {
            _competencyFrameworkService = competencyFrameworkService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets all Competency Frameworks
        /// </summary>
        /// <remarks>
        /// Returns a list of all competency frameworks (without competencies).
        /// </remarks>
        [HttpGet("competencyframeworks")]
        [ProducesResponseType(typeof(IEnumerable<CompetencyFramework>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCompetencyFrameworks")]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var list = await _competencyFrameworkService.GetAsync(ct);
            return Ok(list);
        }

        /// <summary>
        /// Gets a specific Competency Framework by id
        /// </summary>
        /// <remarks>
        /// Returns the framework with all competencies and relationships.
        /// </remarks>
        /// <param name="id">The id of the Competency Framework</param>
        /// <param name="ct"></param>
        [HttpGet("competencyframeworks/{id}")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getCompetencyFramework")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var framework = await _competencyFrameworkService.GetAsync(id, ct);
            return Ok(framework);
        }

        /// <summary>
        /// Creates a new Competency Framework
        /// </summary>
        /// <param name="competencyFramework">The data to create the CompetencyFramework with</param>
        /// <param name="ct"></param>
        [HttpPost("competencyframeworks")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCompetencyFramework")]
        public async Task<IActionResult> Create([FromBody] CompetencyFramework competencyFramework, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var created = await _competencyFrameworkService.CreateAsync(competencyFramework, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }

        /// <summary>
        /// Updates a Competency Framework
        /// </summary>
        /// <param name="id">The id of the CompetencyFramework to update</param>
        /// <param name="competencyFramework">The updated data</param>
        /// <param name="ct"></param>
        [HttpPut("competencyframeworks/{id}")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCompetencyFramework")]
        public async Task<IActionResult> Update(Guid id, [FromBody] CompetencyFramework competencyFramework, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var updated = await _competencyFrameworkService.UpdateAsync(id, competencyFramework, ct);
            return Ok(updated);
        }

        /// <summary>
        /// Imports a Competency Framework from a Moodle-format CSV
        /// </summary>
        /// <remarks>
        /// Accepts a CSV file in the Moodle lpimportcsv 14-column format.
        /// Creates the framework, all competencies with hierarchy, and cross-reference relationships.
        /// </remarks>
        /// <param name="file">The CSV file</param>
        /// <param name="source">Framework source (e.g. "NICE", "DCWF")</param>
        /// <param name="version">Framework version (e.g. "5.1")</param>
        /// <param name="ct"></param>
        [HttpPost("competencyframeworks/import")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "importCompetencyFramework")]
        public async Task<IActionResult> Import(IFormFile file, [FromQuery] string source, [FromQuery] string version, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            if (file == null || file.Length == 0)
                return BadRequest("No file provided.");

            using var stream = file.OpenReadStream();
            var framework = await _competencyFrameworkService.ImportFromMoodleCsvAsync(stream, source, version, ct);
            return CreatedAtAction(nameof(Get), new { id = framework.Id }, framework);
        }

        /// <summary>
        /// Imports a Competency Framework from a NICE-format JSON file
        /// </summary>
        /// <remarks>
        /// Accepts a JSON file in the NICE/NIST CPRT format (response.elements with documents, elements, and relationships).
        /// Creates the framework, all competencies with hierarchy, and work-role-to-TKSA relationships.
        /// </remarks>
        /// <param name="file">The JSON file</param>
        /// <param name="ct"></param>
        [HttpPost("competencyframeworks/import-json")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "importCompetencyFrameworkJson")]
        public async Task<IActionResult> ImportJson(IFormFile file, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            if (file == null || file.Length == 0)
                return BadRequest("No file provided.");

            using var stream = file.OpenReadStream();
            var framework = await _competencyFrameworkService.ImportFromJsonAsync(stream, ct);
            return CreatedAtAction(nameof(Get), new { id = framework.Id }, framework);
        }

        /// <summary>
        /// Imports a Competency Framework from a DCWF-format XLSX file
        /// </summary>
        /// <remarks>
        /// Accepts an XLSX file with columns: ID, Name, Description, ParentID, RelatedIDs.
        /// Creates the framework, all competencies with hierarchy, and cross-reference relationships.
        /// </remarks>
        /// <param name="file">The XLSX file</param>
        /// <param name="source">Framework source (e.g. "DCWF")</param>
        /// <param name="version">Framework version (e.g. "1.0")</param>
        /// <param name="ct"></param>
        [HttpPost("competencyframeworks/import-xlsx")]
        [ProducesResponseType(typeof(CompetencyFramework), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "importCompetencyFrameworkXlsx")]
        public async Task<IActionResult> ImportXlsx(IFormFile file, [FromQuery] string source, [FromQuery] string version, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            if (file == null || file.Length == 0)
                return BadRequest("No file provided.");

            using var stream = file.OpenReadStream();
            var framework = await _competencyFrameworkService.ImportFromDcwfXlsxAsync(stream, source, version, ct);
            return CreatedAtAction(nameof(Get), new { id = framework.Id }, framework);
        }

        /// <summary>
        /// Creates a new Competency within a Framework
        /// </summary>
        /// <param name="frameworkId">The id of the parent CompetencyFramework</param>
        /// <param name="competency">The data to create the Competency with</param>
        /// <param name="ct"></param>
        [HttpPost("competencyframeworks/{frameworkId}/competencies")]
        [ProducesResponseType(typeof(Competency), (int)HttpStatusCode.Created)]
        [SwaggerOperation(OperationId = "createCompetency")]
        public async Task<IActionResult> CreateCompetency(Guid frameworkId, [FromBody] Competency competency, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var created = await _competencyFrameworkService.CreateCompetencyAsync(frameworkId, competency, ct);
            return CreatedAtAction(nameof(Get), new { id = frameworkId }, created);
        }

        /// <summary>
        /// Updates a Competency
        /// </summary>
        /// <param name="competencyId">The id of the Competency to update</param>
        /// <param name="competency">The updated data</param>
        /// <param name="ct"></param>
        [HttpPut("competencies/{competencyId}")]
        [ProducesResponseType(typeof(Competency), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "updateCompetency")]
        public async Task<IActionResult> UpdateCompetency(Guid competencyId, [FromBody] Competency competency, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            var updated = await _competencyFrameworkService.UpdateCompetencyAsync(competencyId, competency, ct);
            return Ok(updated);
        }

        /// <summary>
        /// Deletes a Competency
        /// </summary>
        /// <param name="competencyId">The id of the Competency to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("competencies/{competencyId}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCompetency")]
        public async Task<IActionResult> DeleteCompetency(Guid competencyId, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _competencyFrameworkService.DeleteCompetencyAsync(competencyId, ct);
            return NoContent();
        }

        /// <summary>
        /// Preview a Competency Framework from a Moodle CSV file
        /// </summary>
        /// <remarks>
        /// Returns preview information: element counts, relationships, source/version.
        /// </remarks>
        /// <param name="file">The CSV file</param>
        /// <param name="source">Framework source (e.g. "NICE", "DCWF")</param>
        /// <param name="version">Framework version (e.g. "5.1")</param>
        /// <param name="ct"></param>
        [HttpPost("competencyframeworks/preview-csv")]
        [ProducesResponseType(typeof(CompetencyFrameworkImportPreview), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "previewCompetencyFrameworkCsv")]
        public async Task<IActionResult> PreviewCsv(IFormFile file, [FromQuery] string source, [FromQuery] string version, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file provided.");

            using var stream = file.OpenReadStream();
            var preview = await _competencyFrameworkService.PreviewCsvAsync(stream, source, version, ct);
            return Ok(preview);
        }

        /// <summary>
        /// Preview a Competency Framework from a NICE JSON file
        /// </summary>
        /// <remarks>
        /// Returns preview information: element counts, relationships, source/version.
        /// </remarks>
        /// <param name="file">The JSON file</param>
        /// <param name="ct"></param>
        [HttpPost("competencyframeworks/preview-json")]
        [ProducesResponseType(typeof(CompetencyFrameworkImportPreview), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "previewCompetencyFrameworkJson")]
        public async Task<IActionResult> PreviewJson(IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file provided.");

            using var stream = file.OpenReadStream();
            var preview = await _competencyFrameworkService.PreviewJsonAsync(stream, ct);
            return Ok(preview);
        }

        /// <summary>
        /// Preview a Competency Framework from a DCWF XLSX file
        /// </summary>
        /// <remarks>
        /// Returns preview information: element counts, relationships, source/version.
        /// </remarks>
        /// <param name="file">The XLSX file</param>
        /// <param name="source">Framework source (e.g. "DCWF")</param>
        /// <param name="version">Framework version (e.g. "1.0")</param>
        /// <param name="ct"></param>
        [HttpPost("competencyframeworks/preview-xlsx")]
        [ProducesResponseType(typeof(CompetencyFrameworkImportPreview), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "previewCompetencyFrameworkXlsx")]
        public async Task<IActionResult> PreviewXlsx(IFormFile file, [FromQuery] string source, [FromQuery] string version, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file provided.");

            using var stream = file.OpenReadStream();
            var preview = await _competencyFrameworkService.PreviewXlsxAsync(stream, source, version, ct);
            return Ok(preview);
        }

        /// <summary>
        /// Checks if a Competency Framework can be deleted
        /// </summary>
        /// <remarks>
        /// Returns dependency information showing which MSELs, data fields, and teams are using competencies from this framework.
        /// </remarks>
        /// <param name="id">The id of the Competency Framework to check</param>
        /// <param name="ct"></param>
        [HttpGet("competencyframeworks/{id}/can-delete")]
        [ProducesResponseType(typeof(FrameworkDeleteCheck), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "checkCanDeleteCompetencyFramework")]
        public async Task<IActionResult> CheckCanDelete(Guid id, CancellationToken ct)
        {
            var result = await _competencyFrameworkService.CheckCanDeleteAsync(id, ct);
            return Ok(result);
        }

        /// <summary>
        /// Deletes a Competency Framework
        /// </summary>
        /// <remarks>
        /// Deletes the framework and all associated competencies and relationships (cascade).
        /// Will fail with BadRequest if the framework is in use by any MSELs, data fields, or teams.
        /// </remarks>
        /// <param name="id">The id of the Competency Framework to delete</param>
        /// <param name="ct"></param>
        [HttpDelete("competencyframeworks/{id}")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteCompetencyFramework")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            if (!await _authorizationService.AuthorizeAsync([Data.Enumerations.SystemPermission.ManageCompetencyFrameworks], ct))
                throw new ForbiddenException();

            await _competencyFrameworkService.DeleteAsync(id, ct);
            return NoContent();
        }
    }
}
