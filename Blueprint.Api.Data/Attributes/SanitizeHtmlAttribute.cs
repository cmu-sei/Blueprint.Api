using System;

namespace Blueprint.Api.Data.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class SanitizeHtmlAttribute : Attribute
{
}