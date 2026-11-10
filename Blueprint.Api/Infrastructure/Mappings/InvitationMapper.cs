// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>Invitations' mappings.</summary>
[Mapper]
public static partial class InvitationMapper
{
    public static partial Invitation ToInvitation(this InvitationEntity source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial InvitationEntity ToEntity(this InvitationCreate source);

    [MapperRequiredMapping(RequiredMappingStrategy.Source)]
    public static partial void ApplyTo(this InvitationEdit source, InvitationEntity target);
}
