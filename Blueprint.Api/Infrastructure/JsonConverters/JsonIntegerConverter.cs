// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blueprint.Api.Infrastructure.JsonConverters
{
    class JsonIntegerConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var chkValue = reader.GetString();
                return int.Parse(chkValue);
            }
            return reader.GetInt32();
        }

        public override void Write( Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }

    }
}
