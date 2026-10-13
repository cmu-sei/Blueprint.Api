// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields a Data Option's create and edit requests share. The owning Data Field is not one of
    /// them: only <see cref="DataOptionCreate"/> names it, so an edit cannot move an option to another
    /// field.
    /// </summary>
    public abstract class DataOptionFields
    {
        public string OptionName { get; set; }
        public string OptionValue { get; set; }
        public string OptionDescription { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class DataOptionCreate : DataOptionFields
    {
        public Guid Id { get; set; }
        public Guid DataFieldId { get; set; }
    }

    public class DataOptionEdit : DataOptionFields
    {
    }

    /// <summary>
    /// An option sent as part of its Data Field's create or edit request. On an edit, an id that matches
    /// one of the field's own options updates it; any other id adds a new option.
    /// </summary>
    public class DataFieldOption : DataOptionFields
    {
        public Guid Id { get; set; }
    }
}
