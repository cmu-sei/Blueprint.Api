// Copyright 2024 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Attributes;
using Ganss.Xss;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blueprint.Api.Infrastructure.EventHandlers;

public class SanitizerInterceptor : ISaveChangesInterceptor
{
    private readonly IHtmlSanitizer _sanitizer;

    public SanitizerInterceptor(IHtmlSanitizer sanitizer)
    {
        _sanitizer = sanitizer;
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Sanitize(eventData.Context);
        return result;
    }
    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Sanitize(eventData.Context);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    private void Sanitize(DbContext db)
    {
        if (db == null) return;

        foreach (var entry in db.ChangeTracker.Entries())
        {
            foreach (var prop in GetPropertiesToSanitize(entry))
            {
                var original = (string)prop.GetValue(entry.Entity);
                var sanitized = _sanitizer.Sanitize(original);
                prop.SetValue(entry.Entity, sanitized);
            }
        }
    }

    private IEnumerable<PropertyInfo> GetPropertiesToSanitize(EntityEntry entry)
    {
        if (entry.State is not EntityState.Added and not EntityState.Modified)
            return [];

        var properties = entry.Entity.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p =>
                p.PropertyType == typeof(string) &&
                p.CanRead &&
                p.CanWrite &&
                Attribute.IsDefined(p, typeof(SanitizeHtmlAttribute)));

        return properties;
    }
}