// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Blueprint.Api.Infrastructure.Exceptions;
using Blueprint.Api.ViewModels;
using System;
using System.Net;

namespace Blueprint.Api.Infrastructure.Filters
{
    public class JsonExceptionFilter : IExceptionFilter
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<JsonExceptionFilter> _logger;

        public JsonExceptionFilter(IWebHostEnvironment env, ILogger<JsonExceptionFilter> logger)
        {
            _env = env;
            _logger = logger;
        }

        public void OnException(ExceptionContext context)
        {
            var error = new ApiError();
            error.Status = GetStatusCodeFromException(context.Exception);

            if(error.Status == (int)HttpStatusCode.InternalServerError)
            {
                // an unexpected error is otherwise only in the response the caller got
                _logger.LogError(context.Exception, "Unhandled error on {Method} {Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path);
                if (_env.IsDevelopment())
                {
                    error.Title = context.Exception.Message;
                    error.Detail = context.Exception.StackTrace;
                }
                else
                {
                    error.Title = "A server error occurred.";
                    error.Detail = context.Exception.Message;
                }
            }
            else
            {
                error.Title = context.Exception.Message;
            }

            context.Result = new JsonResult(error)
            {
                StatusCode = error.Status
            };
        }

        /// <summary>
        /// map all custom exceptions to proper http status code
        /// </summary>
        /// <returns></returns>
        private static int GetStatusCodeFromException(Exception exception)
        {
            HttpStatusCode statusCode = HttpStatusCode.InternalServerError;

            if (exception is IApiException)
            {
                statusCode = (exception as IApiException).GetStatusCode();
            }

            return (int)statusCode;
        }
    }
}

