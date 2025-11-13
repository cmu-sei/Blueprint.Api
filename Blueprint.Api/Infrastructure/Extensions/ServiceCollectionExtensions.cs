// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.CodeAnalysis;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Infrastructure.OperationFilters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Net.Http;
using Cite.Api.Client;
using Gallery.Api.Client;
using Player.Api.Client;
using Steamfitter.Api.Client;
using Ganss.Xss;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Blueprint.Api.Infrastructure.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static void AddSwagger(this IServiceCollection services, AuthorizationOptions authOptions)
        {
            // XML Comments path
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string commentsFileName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".xml";
            string commentsFile = Path.Combine(baseDirectory, commentsFileName);

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Blueprint API", Version = "v1" });

                c.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.OAuth2,
                    Flows = new OpenApiOAuthFlows
                    {
                        AuthorizationCode = new OpenApiOAuthFlow
                        {
                            AuthorizationUrl = new Uri(authOptions.AuthorizationUrl),
                            TokenUrl = new Uri(authOptions.TokenUrl),
                            Scopes = new Dictionary<string, string>()
                            {
                                {authOptions.AuthorizationScope, "public api access"}
                            }
                        }
                    }
                });

                c.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
                {
                    { new OpenApiSecuritySchemeReference("oauth2"), [authOptions.AuthorizationScope] }
                });

                c.IncludeXmlComments(commentsFile);
                c.EnableAnnotations();
                c.OperationFilter<DefaultResponseOperationFilter>();
                c.MapType<Optional<Guid?>>(() => new OpenApiSchema
                {
                    OneOf = new List<IOpenApiSchema>
                    {
                        new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                        new OpenApiSchema { Type = JsonSchemaType.Null }
                    }
                });

                c.MapType<JsonElement?>(() => new OpenApiSchema
                {
                    OneOf = new List<IOpenApiSchema>
                    {
                        new OpenApiSchema { Type = JsonSchemaType.Object },
                        new OpenApiSchema { Type = JsonSchemaType.Null }
                    }
                });
            });
        }

        public static void AddPlayerApiClient(this IServiceCollection services)
        {
            services.AddScoped<IPlayerApiClient, PlayerApiClient>(p =>
            {
                var httpContextAccessor = p.GetRequiredService<IHttpContextAccessor>();
                var httpClientFactory = p.GetRequiredService<IHttpClientFactory>();
                var clientOptions = p.GetRequiredService<ClientOptions>();
                // TODO:  figure out why this is needed!
                if (httpContextAccessor.HttpContext == null)
                {
                    return null;
                }

                var playerUri = new Uri(clientOptions.PlayerApiUrl);

                string authHeader = httpContextAccessor.HttpContext.Request.Headers["Authorization"];

                var httpClient = httpClientFactory.CreateClient();
                httpClient.BaseAddress = playerUri;
                httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);

                var apiClient = new PlayerApiClient(httpClient);
                return apiClient;
            });
        }

        public static void AddCiteApiClient(this IServiceCollection services)
        {
            services.AddScoped<ICiteApiClient, CiteApiClient>(p =>
            {
                var httpContextAccessor = p.GetRequiredService<IHttpContextAccessor>();
                var httpClientFactory = p.GetRequiredService<IHttpClientFactory>();
                var clientOptions = p.GetRequiredService<ClientOptions>();

                var citeUri = new Uri(clientOptions.CiteApiUrl);

                string authHeader = httpContextAccessor.HttpContext.Request.Headers["Authorization"];

                var httpClient = httpClientFactory.CreateClient();
                httpClient.BaseAddress = citeUri;
                httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);

                var apiClient = new CiteApiClient(httpClient);
                return apiClient;
            });
        }

        public static void AddGalleryApiClient(this IServiceCollection services)
        {
            services.AddScoped<IGalleryApiClient, GalleryApiClient>(p =>
            {
                var httpContextAccessor = p.GetRequiredService<IHttpContextAccessor>();
                var httpClientFactory = p.GetRequiredService<IHttpClientFactory>();
                var clientOptions = p.GetRequiredService<ClientOptions>();

                var galleryUri = new Uri(clientOptions.GalleryApiUrl);

                string authHeader = httpContextAccessor.HttpContext.Request.Headers["Authorization"];

                var httpClient = httpClientFactory.CreateClient();
                httpClient.BaseAddress = galleryUri;
                httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);

                var apiClient = new GalleryApiClient(httpClient);
                return apiClient;
            });
        }

        public static void AddSteamfitterApiClient(this IServiceCollection services)
        {
            services.AddScoped<ISteamfitterApiClient, SteamfitterApiClient>(p =>
            {
                var httpContextAccessor = p.GetRequiredService<IHttpContextAccessor>();
                var httpClientFactory = p.GetRequiredService<IHttpClientFactory>();
                var clientOptions = p.GetRequiredService<ClientOptions>();

                var steamfitterUri = new Uri(clientOptions.SteamfitterApiUrl);

                string authHeader = httpContextAccessor.HttpContext.Request.Headers["Authorization"];

                var httpClient = httpClientFactory.CreateClient();
                httpClient.BaseAddress = steamfitterUri;
                httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);

                var apiClient = new SteamfitterApiClient(httpClient);
                return apiClient;
            });
        }

        public static void AddHtmlSanitizer(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<Options.HtmlSanitizerOptions>(configuration.GetSection("HtmlSanitizer"));

            services.AddSingleton<IHtmlSanitizer>(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<Options.HtmlSanitizerOptions>>().Value;

                var sanitizer = new HtmlSanitizer();

                foreach (var tag in options.AllowedTags)
                {
                    sanitizer.AllowedTags.Add(tag);
                }

                foreach (var attribute in options.AllowedAttributes)
                {
                    sanitizer.AllowedAttributes.Add(attribute);
                }

                foreach (var cls in options.AllowedClasses)
                {
                    sanitizer.AllowedClasses.Add(cls);
                }

                foreach (var prop in options.AllowedCssProperties)
                {
                    sanitizer.AllowedCssProperties.Add(prop);
                }

                foreach (var scheme in options.AllowedSchemes)
                {
                    sanitizer.AllowedSchemes.Add(scheme);
                }

                return sanitizer;
            });
        }
    }
}
