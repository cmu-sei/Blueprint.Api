// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;

namespace Blueprint.Api.ViewModels
{
    /// <summary>
    /// The fields an Organization's create and edit requests share. The owning MSEL and the template flag
    /// are not among them: only <see cref="OrganizationCreate"/> names them, so an edit cannot move an
    /// Organization between MSELs or between a MSEL and the templates.
    /// </summary>
    public abstract class OrganizationFields
    {
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Description { get; set; }
        public string Summary { get; set; }
        public string Email { get; set; }
    }

    public class OrganizationCreate : OrganizationFields
    {
        public Guid Id { get; set; }
        public Guid? MselId { get; set; }
        public bool IsTemplate { get; set; }
    }

    public class OrganizationEdit : OrganizationFields
    {
    }
}
