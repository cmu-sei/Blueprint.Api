// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Services;
using Blueprint.Api.ViewModels;
using Swashbuckle.AspNetCore.Annotations;

namespace Blueprint.Api.Controllers
{
    public class MselCasterExportController : BaseController
    {
        private readonly ICasterExportService _casterExportService;
        private readonly IMselService _mselService;
        private readonly IBlueprintAuthorizationService _authorizationService;

        public MselCasterExportController(ICasterExportService casterExportService, IMselService mselService, IBlueprintAuthorizationService authorizationService)
        {
            _casterExportService = casterExportService;
            _mselService = mselService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Gets the copy of its Caster directory a Msel carries
        /// </summary>
        /// <remarks>
        /// Returns the Caster directory the Msel carries to another Caster: the project it came from, the directories
        /// from the project's root down to it with their shared files, and the directory's own contents. Downloading
        /// the Msel's JSON file refreshes it from the linked directory. A Msel without one answers 204. File contents
        /// are included only for EditMsels, the Msel's owner and its editors, since a workspace's files can hold
        /// credentials; other contributors get the paths alone.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{id}/caster/export")]
        [ProducesResponseType(typeof(MselCasterExport), (int)HttpStatusCode.OK)]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "getMselCasterExport")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var hasEditMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var export = await _casterExportService.GetAsync(id, hasSystemPermission, hasCreateMselsPermission, hasEditMselsPermission, ct);
            return export == null ? NoContent() : Ok(export);
        }

        /// <summary>
        /// Copies a Msel's Caster directory into the Msel
        /// </summary>
        /// <remarks>
        /// Exports the Msel's linked Caster directory, and the directories above it, as the caller, and stores the
        /// copy in place of any the Msel had. The caller must be able to view the directory and its project in
        /// Caster. Open to EditMsels, the Msel's owner and its editors. Not while the Msel is pushed.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/caster/export")]
        [ProducesResponseType(typeof(MselCasterExport), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "captureMselCasterExport")]
        public async Task<IActionResult> Capture(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var export = await _casterExportService.CaptureAsync(id, hasSystemPermission, ct);
            return Ok(export);
        }

        /// <summary>
        /// Imports a Msel's copy of a Caster directory into Caster
        /// </summary>
        /// <remarks>
        /// Creates a new Caster project, as the caller, from the copy the Msel carries, and links the Msel to the
        /// directory it creates. The caller needs Caster's Create Projects permission. Pushing a Msel that has a copy
        /// and no directory does the same, as the pusher. A template's launches need a directory, so a template with
        /// only a copy is imported here first. Not while the Msel is pushed, nor while it has a directory.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpPost("msels/{id}/caster/export/import")]
        [ProducesResponseType(typeof(MselCasterImportResult), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "importMselCasterExport")]
        public async Task<IActionResult> Import(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            var result = await _casterExportService.ImportAsync(id, hasSystemPermission, ct);
            result.Msel = await _mselService.GetAsync(id, true, ct);
            return Ok(result);
        }

        /// <summary>
        /// Deletes the copy of its Caster directory a Msel carries
        /// </summary>
        /// <remarks>
        /// Deletes the Msel's copy. A Msel that links a Caster directory takes a new copy on its next JSON download.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpDelete("msels/{id}/caster/export")]
        [ProducesResponseType((int)HttpStatusCode.NoContent)]
        [SwaggerOperation(OperationId = "deleteMselCasterExport")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.EditMsels], ct);
            await _casterExportService.DeleteAsync(id, hasSystemPermission, ct);
            return NoContent();
        }

        /// <summary>
        /// Gets the variables a Msel's teams can be mapped to in its Caster topology
        /// </summary>
        /// <remarks>
        /// Returns the team variables of the Msel's linked Caster directory, read from Caster as the caller, or, for
        /// a Msel with no directory, of the copy it carries. Empty when it has neither.
        /// </remarks>
        /// <param name="id">The id of the Msel</param>
        /// <param name="ct"></param>
        [HttpGet("msels/{id}/caster/team-variables")]
        [ProducesResponseType(typeof(IEnumerable<CasterTeamVariable>), (int)HttpStatusCode.OK)]
        [SwaggerOperation(OperationId = "getMselCasterTeamVariables")]
        public async Task<IActionResult> GetTeamVariables(Guid id, CancellationToken ct)
        {
            var hasSystemPermission = await _authorizationService.AuthorizeAsync([SystemPermission.ViewMsels], ct);
            var hasCreateMselsPermission = await _authorizationService.AuthorizeAsync([SystemPermission.CreateMsels], ct);
            var list = await _casterExportService.GetTeamVariablesAsync(id, hasSystemPermission, hasCreateMselsPermission, ct);
            return Ok(list);
        }
    }
}
