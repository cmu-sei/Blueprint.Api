// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Blueprint.Api.Data;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.Infrastructure.Extensions;

namespace Blueprint.Api.Services
{
    public interface IMselImageService
    {
        Task<MselImageEntity> GetAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Msel> SetAsync(Guid mselId, IFormFile file, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Msel> DeleteAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        /// <summary>Makes the MSEL's image with the image model, from its scenario and what the planner asks for, replacing the one it has</summary>
        Task<ViewModels.Msel> GenerateAsync(Guid mselId, string prompt, bool hasSystemPermission, CancellationToken ct);
    }

    public class MselImageService : IMselImageService
    {
        public const int MaxImageBytes = 2 * 1024 * 1024;

        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMselService _mselService;
        private readonly IAiImageClient _imageClient;
        private readonly IAiUsageContext _usageContext;

        public MselImageService(BlueprintContext context, IPrincipal user, IMselService mselService, IAiImageClient imageClient, IAiUsageContext usageContext)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mselService = mselService;
            _imageClient = imageClient;
            _usageContext = usageContext;
        }

        public async Task<ViewModels.Msel> GenerateAsync(Guid mselId, string prompt, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);
            if (!_imageClient.IsConfigured)
                throw new BadRequestException("This server has no image model, so it cannot make a picture. Upload one instead.");

            // the scene the draft described, where the MSEL was drafted; otherwise what the MSEL says of itself
            var draft = MselBuildState.Deserialize(msel.AiDraft)?.Draft;
            var scene = string.Join(" ", new[] { draft?.Scenario?.Problem, draft?.Scenario?.Environment }.Where(t => !string.IsNullOrWhiteSpace(t)));
            if (string.IsNullOrWhiteSpace(scene))
                scene = string.IsNullOrWhiteSpace(msel.Description) ? msel.Name : msel.Description;
            if (string.IsNullOrWhiteSpace(scene))
                throw new BadRequestException("Give the MSEL a description first; the picture is made from it.");

            // an image model weights the start of its prompt and all but ignores the end of a long one, so the planner's
            // direction leads, and the scene follows as a short setting
            var subject = string.IsNullOrWhiteSpace(prompt)
                ? Shorten(scene, MaxScene * 2)
                : prompt.Trim().TrimEnd('.') + ". Setting: " + Shorten(scene, MaxScene);
            _usageContext.Use("image", mselId);
            var dataUri = await _imageClient.PhotoAsync("An establishing shot for a cyber exercise: " + subject, ct);
            var comma = dataUri?.IndexOf(',') ?? -1;
            if (comma < 0 || !dataUri.StartsWith("data:", StringComparison.Ordinal))
                throw new InvalidOperationException("The image model returned nothing usable. Try again.");
            var data = Convert.FromBase64String(dataUri[(comma + 1)..]);
            var contentType = DetectContentType(data) ?? dataUri[5..dataUri.IndexOf(';')];

            if (!await ReplaceImageAsync(mselId, data, contentType, ct))
                _context.MselImages.Add(new MselImageEntity { MselId = mselId, Data = data, ContentType = contentType });
            msel.ImageDateModified = DateTime.UtcNow;
            msel.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);
            return await _mselService.GetAsync(mselId, true, ct);
        }

        public async Task<MselImageEntity> GetAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            // the same people who can read the MSEL, which includes everyone on one of its teams,
            // so the image shows on the join page; templates are readable by anyone, as in MselService.GetAsync
            if (!hasSystemPermission && !msel.IsTemplate && !(await MselViewRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            var image = await _context.MselImages.AsNoTracking().SingleOrDefaultAsync(i => i.MselId == mselId, ct);
            if (image == null)
                throw new EntityNotFoundException<MselImageEntity>();

            return image;
        }

        public async Task<ViewModels.Msel> SetAsync(Guid mselId, IFormFile file, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);

            if (file == null || file.Length == 0)
                throw new BadRequestException("No image was uploaded.");
            if (file.Length > MaxImageBytes)
                throw new BadRequestException($"The image must be {MaxImageBytes / (1024 * 1024)} MB or smaller.");

            byte[] data;
            using (var stream = new MemoryStream())
            {
                await file.CopyToAsync(stream, ct);
                data = stream.ToArray();
            }

            // the type comes from the bytes, not from what the client claims, and SVG is never
            // accepted, because it can carry script
            var contentType = DetectContentType(data);
            if (contentType == null)
                throw new BadRequestException("The image must be a PNG, JPEG, GIF or WebP file.");

            // Overwrite the image in place, so the old one, up to MaxImageBytes, is never read back;
            // add a row only when the MSEL has none.
            var replaced = await ReplaceImageAsync(mselId, data, contentType, ct);
            var added = replaced ? null : _context.MselImages.Add(new MselImageEntity { MselId = mselId, Data = data, ContentType = contentType });
            msel.ImageDateModified = DateTime.UtcNow;
            msel.ModifiedBy = _user.GetId();
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (added != null && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // another first upload added the row after this one looked, so overwrite that row instead
                added.State = EntityState.Detached;
                await ReplaceImageAsync(mselId, data, contentType, ct);
                await _context.SaveChangesAsync(ct);
            }

            // the whole MSEL, as MselService.UpdateAsync returns it, not just its own row
            return await _mselService.GetAsync(mselId, true, ct);
        }

        private async Task<bool> ReplaceImageAsync(Guid mselId, byte[] data, string contentType, CancellationToken ct)
        {
            return await _context.MselImages
                .Where(i => i.MselId == mselId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Data, data)
                    .SetProperty(i => i.ContentType, contentType), ct) > 0;
        }

        public async Task<ViewModels.Msel> DeleteAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await GetEditableMselAsync(mselId, hasSystemPermission, ct);

            var image = await _context.MselImages.SingleOrDefaultAsync(i => i.MselId == mselId, ct);
            if (image != null)
                _context.MselImages.Remove(image);
            msel.ImageDateModified = null;
            msel.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);

            return await _mselService.GetAsync(mselId, true, ct);
        }

        // the same people who can change the MSEL's other details in MselService.UpdateAsync
        private async Task<MselEntity> GetEditableMselAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct)
        {
            var msel = await _context.Msels.SingleOrDefaultAsync(m => m.Id == mselId, ct);
            if (msel == null)
                throw new EntityNotFoundException<MselEntity>();

            if (!hasSystemPermission && !(await MselOwnerRequirement.IsMet(_user.GetId(), mselId, _context)))
                throw new ForbiddenException();

            return msel;
        }

        public static string DetectContentType(byte[] data)
        {
            if (StartsWith(data, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
                return "image/png";
            if (StartsWith(data, 0xFF, 0xD8, 0xFF))
                return "image/jpeg";
            if (StartsWith(data, 0x47, 0x49, 0x46, 0x38))
                return "image/gif";
            if (data.Length >= 12 && StartsWith(data, 0x52, 0x49, 0x46, 0x46) &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
                return "image/webp";
            return null;
        }

        private static bool StartsWith(byte[] data, params byte[] signature)
        {
            return data.Length >= signature.Length && signature.Select((b, i) => data[i] == b).All(match => match);
        }

        // the scene a picture is set in, kept short enough that the image model reads all of it
        private const int MaxScene = 300;

        // the text cut at the last sentence end within length, else at the last space
        public static string Shorten(string text, int length)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (text.Length <= length)
                return text;
            var cut = text[..length];
            var end = cut.LastIndexOfAny(['.', '!', '?']);
            if (end > length / 3)
                return cut[..(end + 1)];
            var space = cut.LastIndexOf(' ');
            return (space > 0 ? cut[..space] : cut).TrimEnd(',', ';', ':');
        }
    }
}
