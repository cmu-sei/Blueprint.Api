// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Blueprint.Api.Data.Models;
using Blueprint.Api.ViewModels;
using Riok.Mapperly.Abstractions;

namespace Blueprint.Api.Infrastructure.Mappings;

/// <summary>
/// A MSEL's own scoring model, read only: a replace is reconciled by MselScoringModelService. Categories and
/// options are in display order, and missing ones are an empty list.
/// </summary>
[Mapper]
public static partial class MselScoringModelMapper
{
    public static partial MselScoringModel ToMselScoringModel(this MselScoringModelEntity source);

    public static partial MselScoringCategory ToMselScoringCategory(this MselScoringCategoryEntity source);

    public static partial MselScoringOption ToMselScoringOption(this MselScoringOptionEntity source);

    private static List<MselScoringCategory> MapCategories(ICollection<MselScoringCategoryEntity> source) =>
        source == null ? [] : source.OrderBy(c => c.DisplayOrder).Select(c => c.ToMselScoringCategory()).ToList();

    private static List<MselScoringOption> MapOptions(ICollection<MselScoringOptionEntity> source) =>
        source == null ? [] : source.OrderBy(o => o.DisplayOrder).Select(o => o.ToMselScoringOption()).ToList();
}
