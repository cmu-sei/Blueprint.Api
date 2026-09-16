// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
using System.Reflection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Blueprint.Api.Infrastructure.OperationFilters
{
    /// <summary>
    /// Names the request body of an action so client generators emit a meaningful parameter name.
    /// An OpenAPI request body has no name of its own, so openapi-generator falls back to
    /// "requestBody" for any [FromBody] parameter that is not a named model. Applying this
    /// attribute restores the action's own parameter name in the generated client.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class RequestBodyNameAttribute : Attribute
    {
        public RequestBodyNameAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    public class RequestBodyNameOperationFilter : IOperationFilter
    {
        // Read by openapi-generator to name the request body parameter it emits.
        private const string ExtensionName = "x-codegen-request-body-name";

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var attribute = context.MethodInfo?.GetCustomAttribute<RequestBodyNameAttribute>();

            if (attribute == null || operation.RequestBody == null)
                return;

            operation.AddExtension(ExtensionName, new JsonNodeExtension(attribute.Name));
        }
    }
}
