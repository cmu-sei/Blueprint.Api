// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System.Linq;
using System.Text.Json.Nodes;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// A schema with everything required, since an answer constrained to its schema leaves out what is optional: a design came
/// back without its cards, metadata and CITE tasks.
/// </summary>
public class MselDraftSchemaTests
{
    [Fact]
    public void RequireAll_RequiresEveryPropertyButAnInjectsColumnValues()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{
              "cards":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"description":{"type":"string"}}}},
              "metadata":{"type":"object","properties":{"language":{"type":"string"}}},
              "injects":{"type":"array","items":{"type":"object","properties":{"minutes":{"type":"integer"},"expectedAction":{"type":"string"},
                "values":{"type":"object","properties":{"Title":{"type":"string"},"Summary":{"type":"string"}}}}}}},
             "required":["cards"]}
            """).AsObject();

        MselDraftService.RequireAll(schema);

        string[] Required(JsonNode node) => node["required"].AsArray().Select(n => n.GetValue<string>()).ToArray();
        Assert.Equal(["cards", "metadata", "injects"], Required(schema));
        Assert.Equal(["name", "description"], Required(schema["properties"]["cards"]["items"]));
        Assert.Equal(["language"], Required(schema["properties"]["metadata"]));
        Assert.Equal(["minutes", "expectedAction", "values"], Required(schema["properties"]["injects"]["items"]));
        // a row fills only the columns that apply to it
        Assert.Null(schema["properties"]["injects"]["items"]["properties"]["values"]["required"]);
    }
}
