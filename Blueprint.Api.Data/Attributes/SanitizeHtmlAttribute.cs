/*
 Copyright 2025 Carnegie Mellon University. All Rights Reserved. 
 Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
*/

using System;

namespace Blueprint.Api.Data.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class SanitizeHtmlAttribute : Attribute
{
}