// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gallery.Api.Client
{
    /// <summary>
    /// Partial class to configure JSON serialization for the Gallery API client.
    /// This allows us to handle string enum values in collections, which is how
    /// Gallery API serializes enum arrays by default.
    /// </summary>
    public partial class GalleryApiClient
    {
        static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
        {
            // Add JsonStringEnumConverter to handle enum values serialized as strings
            // This resolves the TODO(system.text.json) limitation where enum items
            // inside collections need special handling for deserialization
            settings.Converters.Add(new JsonStringEnumConverter());
        }
    }
}
