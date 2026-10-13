// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Proficiency Level's create and edit requests share. The owning scale is not one of
    /// them: only <see cref="ProficiencyLevelCreate"/> names it, so an edit cannot move a level to another
    /// scale.
    /// </summary>
    public abstract class ProficiencyLevelFields
    {
        public string Name { get; set; }
        public int Value { get; set; }
        public string Description { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class ProficiencyLevelCreate : ProficiencyLevelFields
    {
        public Guid Id { get; set; }
        public Guid ProficiencyScaleId { get; set; }
    }

    public class ProficiencyLevelEdit : ProficiencyLevelFields
    {
    }
}
