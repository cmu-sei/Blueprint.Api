// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields an inject type's create and edit requests share. Its Data Fields are not among them:
    /// they are edited through the Data Field endpoints, so an inject type edit cannot reach another
    /// owner's field.
    /// </summary>
    public abstract class InjectTypeFields
    {
        public string Name { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// A new inject type. Its Data Fields, if any, are created as new fields of this inject type.
    /// </summary>
    public class InjectTypeCreate : InjectTypeFields
    {
        public Guid Id { get; set; }
        public ICollection<InjectTypeDataField> DataFields { get; set; } = new List<InjectTypeDataField>();
    }

    public class InjectTypeEdit : InjectTypeFields
    {
    }

    /// <summary>
    /// A Data Field sent with a new inject type. It always becomes a new field of that inject type.
    /// </summary>
    public class InjectTypeDataField : DataFieldFields
    {
    }
}
