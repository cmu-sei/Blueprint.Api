// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Caster.Api.Client;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;

namespace Blueprint.Api.Services
{
    public interface ICasterService
    {
        Task<IEnumerable<ViewModels.CasterDirectory>> GetDirectoriesAsync(CancellationToken ct);
        Task<ViewModels.CasterDirectory> GetDirectoryAsync(Guid directoryId, CancellationToken ct);
        Task<IEnumerable<string>> GetTeamVariablesAsync(Guid directoryId, CancellationToken ct);
    }

    /// <summary>
    /// Caster calls made with the signed-in user's own token, so a planner only sees, and can only
    /// pick, directories Caster lets them view. The push itself runs as the service account.
    /// </summary>
    public class CasterService : ICasterService
    {
        private readonly ICasterApiClient _casterApiClient;

        public CasterService(ICasterApiClient casterApiClient)
        {
            _casterApiClient = casterApiClient;
        }

        public async Task<IEnumerable<ViewModels.CasterDirectory>> GetDirectoriesAsync(CancellationToken ct)
        {
            ICollection<Project> projects;
            try
            {
                // every project needs Caster's ViewProjects permission ...
                projects = await _casterApiClient.GetAllProjectsAsync(false, ct);
            }
            catch (ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.Forbidden)
            {
                // ... otherwise the projects this user is a member of
                projects = await _casterApiClient.GetAllProjectsAsync(true, ct);
            }

            var directories = new List<ViewModels.CasterDirectory>();
            foreach (var project in projects.OrderBy(p => p.Name))
            {
                var projectDirectories = await _casterApiClient.GetDirectoriesByProjectAsync(project.Id, true, false, false, ct);
                directories.AddRange(projectDirectories
                    .OrderBy(d => d.Name)
                    .Select(d => ToViewModel(d, project.Name)));
            }
            return directories;
        }

        /// <summary>
        /// Throws ForbiddenException unless the user can view the directory in Caster.
        /// </summary>
        public async Task<ViewModels.CasterDirectory> GetDirectoryAsync(Guid directoryId, CancellationToken ct)
        {
            try
            {
                var directory = await _casterApiClient.GetDirectoryAsync(directoryId, false, false, ct);
                var project = await _casterApiClient.GetProjectAsync(directory.ProjectId, ct);
                return ToViewModel(directory, project.Name);
            }
            catch (ApiException ex) when (
                ex.StatusCode == (int)HttpStatusCode.Unauthorized ||
                ex.StatusCode == (int)HttpStatusCode.Forbidden ||
                ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                throw new ForbiddenException($"Caster directory {directoryId} does not exist or you cannot view it in Caster.");
            }
        }

        /// <summary>
        /// The variables a team can be mapped to in the template a workspace in this directory runs.
        /// Like a run, it reads the directory's files and those of every directory above it.
        /// </summary>
        public async Task<IEnumerable<string>> GetTeamVariablesAsync(Guid directoryId, CancellationToken ct)
        {
            var files = new List<(string Name, string Content)>();
            Guid? id = directoryId;
            var visited = new HashSet<Guid>();
            try
            {
                while (id != null && visited.Add((Guid)id))
                {
                    var directory = await _casterApiClient.GetDirectoryAsync((Guid)id, false, false, ct);
                    var directoryFiles = await _casterApiClient.GetFilesByDirectoryAsync(directory.Id, true, false, ct);
                    // a workspace's own files, such as the variables a push writes, are not the template's
                    files.AddRange(directoryFiles.Where(f => f.WorkspaceId == null).Select(f => (f.Name, f.Content)));
                    id = directory.ParentId;
                }
            }
            catch (ApiException ex) when (
                ex.StatusCode == (int)HttpStatusCode.Unauthorized ||
                ex.StatusCode == (int)HttpStatusCode.Forbidden ||
                ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                throw new ForbiddenException($"Caster directory {id} does not exist or you cannot view it in Caster.");
            }
            return IntegrationCasterExtensions.FindTeamVariableSlots(files);
        }

        private static ViewModels.CasterDirectory ToViewModel(Directory directory, string projectName)
        {
            return new ViewModels.CasterDirectory
            {
                Id = directory.Id,
                Name = directory.Name,
                ProjectId = directory.ProjectId,
                ProjectName = projectName,
                ParentId = directory.ParentId
            };
        }
    }
}
