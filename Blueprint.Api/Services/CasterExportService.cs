// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Caster.Api.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.ViewModels;

namespace Blueprint.Api.Services
{
    public interface ICasterExportService
    {
        Task<MselCasterExport> GetAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, bool hasEditMselsPermission, CancellationToken ct);
        Task<MselCasterExport> CaptureAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<MselCasterImportResult> ImportAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<MselCasterImportResult> ImportForPushAsync(MselEntity msel, CancellationToken ct);
        Task DeleteAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<IEnumerable<CasterTeamVariable>> GetTeamVariablesAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct);
        Task RefreshForDownloadAsync(Guid mselId, CancellationToken ct);
    }

    /// <summary>
    /// The copy of its Caster directory a MSEL carries to another Caster. It is taken with the signed-in user's
    /// own Caster token, from Caster's directory export plus the directories above it, whose shared files a
    /// workspace also runs with. An import creates a new Caster project, the chain of directories, and imports
    /// the export into the last one, also as the signed-in user: Caster checks that they may create a project,
    /// and they own it. The copy can come from an uploaded file, so it is never imported as the service account.
    /// </summary>
    public class CasterExportService : ICasterExportService
    {
        // A MSEL file carries the archive as base64, and the whole file is read into memory on upload
        public const int MaxArchiveBytes = 20 * 1024 * 1024;
        // what an archive may expand to, so a small upload cannot exhaust memory when it is read
        public const int MaxArchiveEntries = 1000;
        public const int MaxEntryBytes = 5 * 1024 * 1024;
        public const long MaxExpandedBytes = 50L * 1024 * 1024;
        // a MSEL file upload: the archive, as base64, and the rest of the MSEL
        public const long MaxMselFileBytes = 100L * 1024 * 1024;
        private const string WorkspacesEntryName = "__Workspaces__";
        private const string ManifestEntryName = "__Caster__/manifest.json";
        // the workspaces a push creates (IntegrationCasterExtensions.GetWorkspaceName): the push's own, not the topology's
        private static readonly Regex _blueprintWorkspace = new(
            $"(^|/){WorkspacesEntryName}/blueprint-[0-9a-fA-F]{{8}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{12}}/",
            RegexOptions.Compiled);

        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly ICasterApiClient _casterApiClient;
        private readonly ICasterService _casterService;
        private readonly ILogger<CasterExportService> _logger;

        public CasterExportService(BlueprintContext context, IPrincipal user, ICasterApiClient casterApiClient, ICasterService casterService, ILogger<CasterExportService> logger)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _casterApiClient = casterApiClient;
            _casterService = casterService;
            _logger = logger;
        }

        public async Task<MselCasterExport> GetAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, bool hasEditMselsPermission, CancellationToken ct)
        {
            await CheckContributorAsync(mselId, hasSystemPermission, hasCreateMselsPermission, ct);
            var export = await LoadAsync(mselId, ct);
            if (export == null)
                return null;
            // a workspace's files can hold credentials, so only those who may change the copy read its files
            var userId = _user.GetId();
            var includeContents = hasEditMselsPermission ||
                await MselOwnerRequirement.IsMet(userId, mselId, _context) ||
                await MselEditorRequirement.IsMet(userId, mselId, _context);
            return await ToViewModelAsync(export, [], includeContents, ct);
        }

        public async Task<MselCasterExport> CaptureAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            if (msel.CasterDirectoryId == null)
                throw new ConflictException("The MSEL has no Caster directory to copy. Select its Caster directory first.");
            var (export, warnings) = await CaptureFromCasterAsync(msel, ct);
            return await ToViewModelAsync(export, warnings, true, ct);
        }

        // A MSEL file download refreshes the copy of a linked directory, so the file carries the topology as it is
        // now, whether or not Caster is on. It is not held to the integrations' lock: the directory cannot change
        // while the MSEL is pushed. A refresh that fails (Caster down, the directory gone, or the downloader not
        // allowed to view it) keeps the stored copy, so moving a MSEL never depends on Caster.
        public async Task RefreshForDownloadAsync(Guid mselId, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();
            if (msel.CasterDirectoryId == null)
                return;
            try
            {
                await CaptureFromCasterAsync(msel, ct);
            }
            catch (System.Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not copy Caster directory {DirectoryId} for the download of MSEL {MselId}; the file carries the stored copy", msel.CasterDirectoryId, mselId);
                _context.ChangeTracker.Clear();
            }
        }

        public async Task<MselCasterImportResult> ImportAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            var result = await ImportAndLinkAsync(msel, ct);
            // the link bypassed the change tracker, so save the MSEL once more for its MselUpdated broadcast, which
            // an open MSEL page uses to show the new directory. The import itself is done, so a failure here is logged
            // rather than reported as a failed import.
            msel.ModifiedBy = _user.GetId();
            _context.Entry(msel).Property(m => m.ModifiedBy).IsModified = true;
            try
            {
                await _context.SaveChangesAsync(CancellationToken.None);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "MSEL {MselId} was linked to its imported Caster directory, but could not be saved again to notify open pages", mselId);
            }
            return result;
        }

        // A push of a MSEL with a copy and no directory imports the copy first, as the pusher. The push checks who
        // may push.
        public Task<MselCasterImportResult> ImportForPushAsync(MselEntity msel, CancellationToken ct) => ImportAndLinkAsync(msel, ct);

        private async Task<MselCasterImportResult> ImportAndLinkAsync(MselEntity msel, CancellationToken ct)
        {
            if (msel.CasterDirectoryId != null)
                throw new ConflictException("The MSEL already has a Caster directory. Clear it before importing the MSEL's copy into Caster.");
            var export = await LoadAsync(msel.Id, ct);
            if (export == null)
                throw new EntityNotFoundException<MselCasterExportEntity>("The MSEL has no copy of a Caster directory to import.");

            CasterImport imported;
            try
            {
                imported = await ImportToCasterAsync(export, _casterApiClient, _logger, ct);
            }
            catch (ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.Unauthorized || ex.StatusCode == (int)HttpStatusCode.Forbidden)
            {
                throw new ForbiddenException("Caster did not let you create a project for the MSEL's copy of its Caster directory. Ask a Caster administrator for the Create Projects permission.");
            }
            catch (ApiException ex)
            {
                // the project was deleted again, so nothing is left half imported
                throw new ConflictException($"Caster refused the import of the MSEL's copy of its Caster directory with {ex.StatusCode}, and nothing was imported. {CasterProblem(ex)}".TrimEnd());
            }

            // Link the directory only if the MSEL still has none: two imports at once each make a project, and the
            // one that loses deletes its own. Not cancelled, as the project now exists: a link the caller hung up on
            // would leave it orphaned.
            var userId = _user.GetId();
            var directoryId = imported.DirectoryId;
            var linked = await _context.Msels
                .Where(m => m.Id == msel.Id && m.CasterDirectoryId == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.CasterDirectoryId, directoryId)
                    .SetProperty(m => m.ModifiedBy, userId)
                    .SetProperty(m => m.DateModified, DateTime.UtcNow),
                    CancellationToken.None);
            if (linked == 0)
            {
                await DeleteProjectAsync(_casterApiClient, imported.ProjectId, _logger);
                throw new ConflictException("The MSEL was linked to a Caster directory while its copy was being imported, so this import was undone.");
            }
            // the tracked MSEL as the database now has it, so a later save does not write the link again
            var link = _context.Entry(msel).Property(m => m.CasterDirectoryId);
            link.CurrentValue = directoryId;
            link.OriginalValue = directoryId;
            link.IsModified = false;

            return new MselCasterImportResult
            {
                CasterDirectoryId = directoryId,
                LockedFiles = imported.LockedFiles
            };
        }

        public async Task DeleteAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            var export = await _context.MselCasterExports.SingleOrDefaultAsync(e => e.MselId == mselId, ct);
            if (export == null)
                throw new EntityNotFoundException<MselCasterExportEntity>();

            // the directories and files go with it, by cascade
            _context.MselCasterExports.Remove(export);
            msel.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);
        }

        public async Task<IEnumerable<CasterTeamVariable>> GetTeamVariablesAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            await CheckContributorAsync(mselId, hasSystemPermission, hasCreateMselsPermission, ct);
            var msel = await _context.Msels.AsNoTracking().SingleAsync(m => m.Id == mselId, ct);
            if (msel.CasterDirectoryId != null)
                return await _casterService.GetTeamVariablesAsync((Guid)msel.CasterDirectoryId, ct);
            var export = await LoadAsync(mselId, ct);
            return export == null ? [] : IntegrationCasterExtensions.FindTeamVariableSlots(TemplateFiles(export));
        }

        /// <summary>
        /// The files a workspace in the exported directory runs, as a run reads them: the shared files of every
        /// directory above it and its own, but no workspace's and no child directory's.
        /// </summary>
        internal static IEnumerable<(string Name, string Content)> TemplateFiles(MselCasterExportEntity export) =>
            export.Directories.SelectMany(d => d.Files).Select(f => (f.Name, f.Content))
                .Concat(ReadEntries(export.Archive)
                    .Where(e => e.Workspace == null && !e.Path.Contains('/'))
                    .Select(e => (e.Path, e.Content)))
                .ToList();

        /// <summary>
        /// Copies the MSEL's Caster directory into the MSEL, in place of any copy it had, and saves it. Reads
        /// Caster with the user's own token, so the user must be able to view the directory and its project.
        /// </summary>
        private async Task<(MselCasterExportEntity Export, List<string> Warnings)> CaptureFromCasterAsync(MselEntity msel, CancellationToken ct)
        {
            var directoryId = (Guid)msel.CasterDirectoryId;
            var warnings = new List<string>();
            var export = new MselCasterExportEntity();
            try
            {
                // the chain, from the exported directory up to the project's root
                var chain = new List<Caster.Api.Client.Directory>();
                var visited = new HashSet<Guid>();
                Guid? id = directoryId;
                while (id != null && visited.Add((Guid)id))
                {
                    var directory = await _casterApiClient.GetDirectoryAsync((Guid)id, false, false, ct);
                    chain.Insert(0, directory);
                    id = directory.ParentId;
                }
                var target = chain[^1];
                var project = await _casterApiClient.GetProjectAsync(target.ProjectId, ct);

                export.SourceProjectId = project.Id;
                export.ProjectName = project.Name;
                export.SourceDirectoryId = target.Id;
                foreach (var (directory, level) in chain.Select((d, i) => (d, i)))
                {
                    var exportDirectory = new MselCasterExportDirectoryEntity
                    {
                        Level = level,
                        Name = directory.Name,
                        TerraformVersion = directory.TerraformVersion,
                        Parallelism = directory.Parallelism,
                        AzureDestroyFailureThresholdEnabled = directory.AzureDestroyFailureThresholdEnabled,
                        AzureDestroyFailureThreshold = directory.AzureDestroyFailureThreshold
                    };
                    // a directory above the exported one contributes its shared files; its workspaces and its
                    // other children are not this topology's
                    if (directory.Id != target.Id)
                    {
                        var files = await _casterApiClient.GetFilesByDirectoryAsync(directory.Id, true, false, ct);
                        foreach (var file in files.Where(f => f.WorkspaceId == null && !f.IsDeleted).OrderBy(f => f.Name, StringComparer.Ordinal))
                            exportDirectory.Files.Add(new MselCasterExportFileEntity { Name = file.Name, Content = file.Content ?? "" });
                    }
                    export.Directories.Add(exportDirectory);
                }

                // an export does not carry designs, which a run also reads
                foreach (var directory in chain)
                {
                    try
                    {
                        var designs = await _casterApiClient.GetDesignsByDirectoryAsync(directory.Id, ct);
                        foreach (var design in designs.Where(d => d.Enabled))
                            warnings.Add($"The design \"{design.Name}\" in the directory \"{directory.Name}\" is not part of the copy, so an import does not have it.");
                    }
                    catch (ApiException ex)
                    {
                        _logger.LogDebug("Could not read the designs of Caster directory {DirectoryId}: {StatusCode}", directory.Id, ex.StatusCode);
                    }
                }

                using var response = await _casterApiClient.ExportDirectoryAsync(target.Id, ArchiveType.Zip, false, ct);
                using var archive = new MemoryStream();
                await response.Stream.CopyToAsync(archive, ct);
                export.Archive = WithoutBlueprintWorkspaces(archive.ToArray());
            }
            catch (ApiException ex) when (
                ex.StatusCode == (int)HttpStatusCode.Unauthorized ||
                ex.StatusCode == (int)HttpStatusCode.Forbidden ||
                ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                throw new ForbiddenException($"Caster directory {directoryId} does not exist or you cannot view it, its project and its parent directories in Caster.");
            }
            catch (ApiException ex)
            {
                throw new ConflictException($"Caster answered {ex.StatusCode} while exporting directory {directoryId}, so nothing was copied. {CasterProblem(ex)}".TrimEnd());
            }
            if (export.Archive.Length > MaxArchiveBytes)
                throw new ConflictException($"The Caster directory's export is {export.Archive.Length / (1024 * 1024)} MB, more than the {MaxArchiveBytes / (1024 * 1024)} MB a MSEL can carry.");

            try
            {
                Validate(export);
            }
            catch (BadRequestException ex)
            {
                throw new ConflictException($"The Caster directory cannot be copied: {ex.Message}");
            }

            var userId = _user.GetId();
            export.DateExported = DateTime.UtcNow;
            export.ExportedBy = userId;
            AssignIds(export, msel.Id, userId);

            // the copy is keyed by its MSEL, so the new one replaces the old in one transaction: a failed save keeps
            // the old copy, and a copy taken at the same moment makes this one's insert fail rather than mix the two
            await using (var transaction = await _context.Database.BeginTransactionAsync(ct))
            {
                // the directories and files go with it, by cascade
                await _context.MselCasterExports.Where(e => e.MselId == msel.Id).ExecuteDeleteAsync(ct);
                _context.MselCasterExports.Add(export);
                try
                {
                    await _context.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
                {
                    throw new ConflictException("The MSEL's copy of its Caster directory was replaced by another copy at the same time. Try again.");
                }
                await transaction.CommitAsync(ct);
            }

            return (export, warnings);
        }

        private record CasterImport(Guid ProjectId, Guid DirectoryId, List<string> LockedFiles);

        /// <summary>
        /// Recreates <paramref name="export"/> in Caster as a new project: the chain of directories with their
        /// settings, the shared files of the ones above the exported directory, and Caster's own export imported
        /// into the last. A failure deletes the project again, so a retry starts clean.
        /// </summary>
        private static async Task<CasterImport> ImportToCasterAsync(MselCasterExportEntity export, ICasterApiClient casterApiClient, ILogger logger, CancellationToken ct)
        {
            Validate(export);
            var project = await casterApiClient.CreateProjectAsync(new CreateProjectCommand { Name = export.ProjectName }, ct);
            try
            {
                Guid? parentId = null;
                foreach (var level in export.Directories.OrderBy(d => d.Level))
                {
                    var directory = await casterApiClient.CreateDirectoryAsync(new CreateDirectoryCommand
                    {
                        Name = level.Name,
                        ProjectId = project.Id,
                        ParentId = parentId,
                        TerraformVersion = string.IsNullOrWhiteSpace(level.TerraformVersion) ? null : level.TerraformVersion,
                        Parallelism = level.Parallelism,
                        AzureDestroyFailureThreshold = level.AzureDestroyFailureThreshold,
                        AzureDestroyFailureThresholdEnabled = level.AzureDestroyFailureThresholdEnabled
                    }, ct);
                    foreach (var file in level.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
                    {
                        await casterApiClient.CreateFileAsync(new CreateFileCommand
                        {
                            Name = file.Name,
                            DirectoryId = directory.Id,
                            Content = file.Content ?? ""
                        }, ct);
                    }
                    parentId = directory.Id;
                }

                // Caster picks the archive's type by its file name, and reads nothing else from it
                using var archive = new MemoryStream(export.Archive);
                var result = await casterApiClient.ImportDirectoryAsync((Guid)parentId, false, new FileParameter(archive, "export.zip", "application/zip"), ct);
                return new CasterImport(project.Id, (Guid)parentId, result?.LockedFiles?.ToList() ?? []);
            }
            catch
            {
                await DeleteProjectAsync(casterApiClient, project.Id, logger);
                throw;
            }
        }

        private static async Task DeleteProjectAsync(ICasterApiClient casterApiClient, Guid projectId, ILogger logger)
        {
            try
            {
                await casterApiClient.DeleteProjectAsync(projectId, CancellationToken.None);
            }
            catch (System.Exception ex)
            {
                logger.LogError(ex, "Could not delete Caster project {ProjectId} after its import was undone", projectId);
            }
        }

        /// <summary>
        /// Refuses a copy an import could not recreate: no directories, levels that are not 0, 1, 2 ... in order,
        /// an unnamed directory or file, or an archive that is not a zip or is too big. A MSEL file's import runs it,
        /// since the file's copy is read straight into the entity.
        /// </summary>
        internal static void Validate(MselCasterExportEntity export)
        {
            var levels = export.Directories?.OrderBy(d => d.Level).ToList() ?? [];
            if (levels.Count == 0)
                throw new BadRequestException("The copy of the Caster directory has no directories.");
            if (levels.Select((d, i) => d.Level != i).Any(x => x))
                throw new BadRequestException("The copy of the Caster directory has directory levels that are not 0, 1, 2 ... in order.");
            if (levels.Any(d => string.IsNullOrWhiteSpace(d.Name)))
                throw new BadRequestException("The copy of the Caster directory has a directory with no name.");
            if (levels.SelectMany(d => d.Files).Any(f => string.IsNullOrWhiteSpace(f.Name)))
                throw new BadRequestException("The copy of the Caster directory has a file with no name.");
            if (string.IsNullOrWhiteSpace(export.ProjectName))
                throw new BadRequestException("The copy of the Caster directory has no project name.");
            if (export.Archive == null || export.Archive.Length == 0)
                throw new BadRequestException("The copy of the Caster directory has no export.");
            if (export.Archive.Length > MaxArchiveBytes)
                throw new BadRequestException($"The copy of the Caster directory is more than {MaxArchiveBytes / (1024 * 1024)} MB.");
            try
            {
                using var zip = new ZipArchive(new MemoryStream(export.Archive), ZipArchiveMode.Read);
                if (zip.Entries.Count > MaxArchiveEntries)
                    throw new BadRequestException($"The copy of the Caster directory has more than {MaxArchiveEntries} files.");
                // the sizes a zip declares can lie, so each entry is read, and no further than the limits
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    using var stream = entry.Open();
                    var read = CountBytes(stream, MaxEntryBytes + 1L);
                    if (read > MaxEntryBytes)
                        throw new BadRequestException($"The file \"{entry.FullName}\" in the copy of the Caster directory is more than {MaxEntryBytes / (1024 * 1024)} MB.");
                    expanded += read;
                    if (expanded > MaxExpandedBytes)
                        throw new BadRequestException($"The copy of the Caster directory expands to more than {MaxExpandedBytes / (1024 * 1024)} MB.");
                }
            }
            catch (InvalidDataException)
            {
                throw new BadRequestException("The copy of the Caster directory is not a zip file.");
            }
        }

        // reads at most max bytes of the stream, and says how many it read
        private static long CountBytes(Stream stream, long max)
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while (total < max && (n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, max - total))) > 0)
                total += n;
            return total;
        }

        /// <summary>
        /// Makes <paramref name="export"/> the MSEL's: new ids for its directories and files, with their parents.
        /// A capture, a MSEL copy and an import each use it.
        /// </summary>
        internal static void AssignIds(MselCasterExportEntity export, Guid mselId, Guid userId)
        {
            export.MselId = mselId;
            export.CreatedBy = userId;
            foreach (var directory in export.Directories)
            {
                directory.Id = Guid.NewGuid();
                directory.MselCasterExportId = mselId;
                directory.CreatedBy = userId;
                foreach (var file in directory.Files)
                {
                    file.Id = Guid.NewGuid();
                    file.MselCasterExportDirectoryId = directory.Id;
                    file.CreatedBy = userId;
                }
            }
        }

        /// <summary>
        /// The archive without the workspaces a Blueprint push made (blueprint-{msel id}), anywhere in it, and
        /// without their settings in its manifest: they hold a past push's variables, not the topology.
        /// </summary>
        internal static byte[] WithoutBlueprintWorkspaces(byte[] archive)
        {
            using var input = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            if (!input.Entries.Any(e => _blueprintWorkspace.IsMatch(e.FullName)))
                return archive;

            var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var entry in input.Entries.Where(e => !_blueprintWorkspace.IsMatch(e.FullName)))
                {
                    var copy = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    copy.LastWriteTime = entry.LastWriteTime;
                    using var from = entry.Open();
                    using var to = copy.Open();
                    if (entry.FullName == ManifestEntryName)
                        WriteManifestWithoutBlueprintWorkspaces(from, to);
                    else
                        from.CopyTo(to);
                }
            }
            return output.ToArray();
        }

        private static void WriteManifestWithoutBlueprintWorkspaces(Stream from, Stream to)
        {
            using var reader = new StreamReader(from, Encoding.UTF8);
            var text = reader.ReadToEnd();
            JsonNode manifest;
            try
            {
                manifest = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                manifest = null;
            }
            if (manifest?["workspaces"] is JsonObject workspaces)
            {
                foreach (var key in workspaces.Select(w => w.Key).Where(k => _blueprintWorkspace.IsMatch(k.EndsWith('/') ? k : k + "/")).ToList())
                    workspaces.Remove(key);
                text = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            var bytes = new UTF8Encoding(false).GetBytes(text);
            to.Write(bytes, 0, bytes.Length);
        }

        /// <summary>The archive's files, by path, with Caster's manifest left out.</summary>
        internal static List<MselCasterExportEntry> ReadEntries(byte[] archive)
        {
            var entries = new List<MselCasterExportEntry>();
            if (archive == null || archive.Length == 0)
                return entries;
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.StartsWith("__Caster__/")).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                // a stored archive was held to the limits when it was stored; this is no further than the limit
                using var bounded = new MemoryStream();
                using (var stream = entry.Open())
                    CopyBytes(stream, bounded, MaxEntryBytes);
                bounded.Position = 0;
                using var reader = new StreamReader(bounded, new UTF8Encoding(false));
                var segments = entry.FullName.Split('/');
                var workspaceAt = Array.IndexOf(segments, WorkspacesEntryName);
                entries.Add(new MselCasterExportEntry
                {
                    Path = entry.FullName,
                    Workspace = workspaceAt >= 0 && workspaceAt + 1 < segments.Length - 1 ? segments[workspaceAt + 1] : null,
                    Content = reader.ReadToEnd()
                });
            }
            return entries;
        }

        // Caster answers a refused request with a problem document; its title says what was wrong
        private static string CasterProblem(ApiException ex)
        {
            try
            {
                using var problem = JsonDocument.Parse(ex.Response ?? "");
                return problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("title", out var title)
                    ? title.GetString()
                    : "";
            }
            catch (JsonException)
            {
                return "";
            }
        }

        private static void CopyBytes(Stream from, Stream to, long max)
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while (total < max && (n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, max - total))) > 0)
            {
                to.Write(buffer, 0, n);
                total += n;
            }
        }

        private async Task<MselCasterExport> ToViewModelAsync(MselCasterExportEntity export, List<string> warnings, bool includeContents, CancellationToken ct)
        {
            var exportedByName = export.ExportedBy == null ? null :
                await _context.Users.Where(u => u.Id == export.ExportedBy).Select(u => u.Name).SingleOrDefaultAsync(ct);
            return new MselCasterExport
            {
                MselId = export.MselId,
                ProjectName = export.ProjectName,
                SourceProjectId = export.SourceProjectId,
                SourceDirectoryId = export.SourceDirectoryId,
                DateExported = export.DateExported,
                ExportedBy = export.ExportedBy,
                ExportedByName = exportedByName,
                Directories = export.Directories.OrderBy(d => d.Level).Select(d => new MselCasterExportDirectory
                {
                    Level = d.Level,
                    Name = d.Name,
                    TerraformVersion = d.TerraformVersion,
                    Parallelism = d.Parallelism,
                    AzureDestroyFailureThresholdEnabled = d.AzureDestroyFailureThresholdEnabled,
                    AzureDestroyFailureThreshold = d.AzureDestroyFailureThreshold,
                    Files = d.Files.OrderBy(f => f.Name, StringComparer.Ordinal)
                        .Select(f => new MselCasterExportFile { Name = f.Name, Content = includeContents ? f.Content : null })
                        .ToList()
                }).ToList(),
                Entries = ReadEntries(export.Archive)
                    .Select(e => { e.Content = includeContents ? e.Content : null; return e; })
                    .ToList(),
                ContentsIncluded = includeContents,
                Warnings = warnings
            };
        }

        private async Task<MselCasterExportEntity> LoadAsync(Guid mselId, CancellationToken ct) =>
            await _context.MselCasterExports
                .Include(e => e.Directories)
                .ThenInclude(d => d.Files)
                .AsSplitQuery()
                .SingleOrDefaultAsync(e => e.MselId == mselId, ct);

        // the planners' material, as the MSEL's own scoring model is
        private async Task CheckContributorAsync(Guid mselId, bool hasSystemPermission, bool hasCreateMselsPermission, CancellationToken ct)
        {
            if (!await _context.Msels.AnyAsync(m => m.Id == mselId, ct))
                throw new EntityNotFoundException<MselEntity>();
            if (!hasSystemPermission && !(await MselContributorRequirement.IsMet(_user.GetId(), mselId, hasCreateMselsPermission, _context)))
                throw new ForbiddenException();
        }

        // EditMsels, the MSEL's owner or an editor of it, as for its other integration settings, under the
        // integrations' lock: what was pushed was built from them, and only a pull and a new push rebuild it
        private async Task<MselEntity> GetEditableMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            var userId = _user.GetId();
            if (!hasSystemPermission &&
                !await MselOwnerRequirement.IsMet(userId, mselId, _context) &&
                !await MselEditorRequirement.IsMet(userId, mselId, _context))
                throw new ForbiddenException();

            if (MselService.HasIntegrations(msel))
                throw new ConflictException("The Caster copy cannot be changed while the MSEL is pushed, or is being pushed or pulled. Remove Integrations first.");

            return msel;
        }
    }
}
