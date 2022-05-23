// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blueprint.Api.Infrastructure.JsonConverters
{
    class JsonNullableGuidConverter : JsonConverter<Guid?>
    {
        public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string chkValue = reader.GetString();
            if (string.IsNullOrWhiteSpace(chkValue))
            {
                return null;
            }
            return Guid.Parse(chkValue);
        }

        public override void Write( Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case null:
                    writer.WriteStringValue("");
                    break;
                default:
                    writer.WriteStringValue(value.ToString());
                    break;

            }
        }

    }
}
