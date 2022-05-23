// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Blueprint.Api.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Blueprint.Api.Infrastructure.Filters
{
    public class ValidateModelStateFilter : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var modelState = context.ModelState;

            if (!modelState.IsValid)
            {
                ApiError error = new ApiError("Invalid Data", System.Net.HttpStatusCode.BadRequest);

                List<string> errorDetails = modelState.Keys
                    .SelectMany(key => modelState[key].Errors.Select(x => $"{key}: { (string.IsNullOrEmpty(x.ErrorMessage) ? x.Exception.Message : x.ErrorMessage) }"))
                    .ToList();

                error.Detail = string.Join("\n", errorDetails.ToArray());

                context.Result = new BadRequestObjectResult(error);
            }
        }
    }
}

