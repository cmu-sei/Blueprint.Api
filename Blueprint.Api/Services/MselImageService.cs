// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
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

namespace Blueprint.Api.Services
{
    public interface IMselImageService
    {
        Task<MselImageEntity> GetAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Msel> SetAsync(Guid mselId, IFormFile file, bool hasSystemPermission, CancellationToken ct);
        Task<ViewModels.Msel> DeleteAsync(Guid mselId, bool hasSystemPermission, CancellationToken ct);
    }

    public class MselImageService : IMselImageService
    {
        public const int MaxImageBytes = 2 * 1024 * 1024;

        private readonly BlueprintContext _context;
        private readonly ClaimsPrincipal _user;
        private readonly IMapper _mapper;

        public MselImageService(BlueprintContext context, IPrincipal user, IMapper mapper)
        {
            _context = context;
            _user = user as ClaimsPrincipal;
            _mapper = mapper;
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

            var image = await _context.MselImages.SingleOrDefaultAsync(i => i.MselId == mselId, ct);
            if (image == null)
            {
                image = new MselImageEntity { MselId = mselId };
                _context.MselImages.Add(image);
            }
            image.Data = data;
            image.ContentType = contentType;
            msel.ImageDateModified = DateTime.UtcNow;
            msel.ModifiedBy = _user.GetId();
            await _context.SaveChangesAsync(ct);

            return _mapper.Map<ViewModels.Msel>(msel);
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

            return _mapper.Map<ViewModels.Msel>(msel);
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
    }
}
