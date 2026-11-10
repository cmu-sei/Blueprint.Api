// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using Blueprint.Api.Data.Models;

namespace Cite.Api.Client
{
    // The generated scoring model types carry the fields a MSEL's own scoring model shares with CITE, so the
    // conversions in CiteScoringModelConverter copy them through IScoringModelFields and its category and option
    // counterparts. A shared field CITE renames or drops breaks the build here, not the push.

    public partial class ScoringModel : IScoringModelFields { }

    public partial class CreateScoringModelRequest : IScoringModelFields { }

    public partial class ScoringCategory : IScoringCategoryFields { }

    public partial class CreateScoringCategoryRequest : IScoringCategoryFields { }

    public partial class ScoringOption : IScoringOptionFields { }

    public partial class CreateScoringOptionRequest : IScoringOptionFields { }
}
