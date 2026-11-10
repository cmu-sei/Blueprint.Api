// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Cite.Api.Client;

namespace Blueprint.Api.Services
{
    /// <summary>
    /// Converts between a MSEL's own scoring model and the CITE client's. The shared fields are copied through
    /// <see cref="IScoringModelFields"/> and its category and option counterparts. The enums go by name: Blueprint
    /// keeps CITE's stored values (None = 20), the client numbers its copies from 0, and the wire carries the names.
    /// </summary>
    public static class CiteScoringModelConverter
    {
        /// <summary>
        /// The request that creates the model in CITE, with its categories and options as new rows of it, in display
        /// order. CITE gives them their ids.
        /// </summary>
        public static CreateScoringModelRequest ToCite(MselScoringModelEntity model) => model.CopyFieldsTo(new CreateScoringModelRequest
        {
            Status = ItemStatus.Active,
            RightSideDisplay = Enum.Parse<RightSideDisplay>(model.RightSideDisplay.ToString()),
            ScoringCategories = model.Categories.OrderBy(c => c.DisplayOrder).Select(c => c.CopyFieldsTo(new CreateScoringCategoryRequest
            {
                ScoringOptionSelection = Enum.Parse<ScoringOptionSelection>(c.ScoringOptionSelection.ToString()),
                ScoringOptions = c.Options.OrderBy(o => o.DisplayOrder).Select(o => o.CopyFieldsTo(new CreateScoringOptionRequest())).ToList()
            })).ToList()
        });

        /// <summary>A MSEL's own copy of a CITE model, unsaved and with no ids.</summary>
        public static MselScoringModelEntity FromCite(ScoringModel model) => model.CopyFieldsTo(new MselScoringModelEntity
        {
            RightSideDisplay = Enum.Parse<CiteRightSideDisplay>(model.RightSideDisplay.ToString()),
            Categories = (model.ScoringCategories ?? []).OrderBy(c => c.DisplayOrder).Select(c => c.CopyFieldsTo(new MselScoringCategoryEntity
            {
                ScoringOptionSelection = Enum.Parse<CiteScoringOptionSelection>(c.ScoringOptionSelection.ToString()),
                Options = (c.ScoringOptions ?? []).OrderBy(o => o.DisplayOrder).Select(o => o.CopyFieldsTo(new MselScoringOptionEntity())).ToList()
            })).ToList()
        });
    }
}
