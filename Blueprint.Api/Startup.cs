// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Blueprint.Api.Infrastructure.EventHandlers;
using Blueprint.Api.Infrastructure.Extensions;
using Blueprint.Api.Data;
using Blueprint.Api.Infrastructure.JsonConverters;
using Blueprint.Api.Infrastructure.Mapping;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using System;
using Blueprint.Api.Infrastructure;
using Blueprint.Api.Infrastructure.Authorization;
using Blueprint.Api.Infrastructure.Filters;
using System.Linq;
using System.Security.Principal;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.JsonWebTokens;
using AutoMapper.Internal;

namespace Blueprint.Api;

public class Startup
{
    public Infrastructure.Options.AuthorizationOptions _authOptions = new();
    public IConfiguration Configuration { get; }
    private string _pathbase;
    private const string _routePrefix = "api";
    private readonly SignalROptions _signalROptions = new();

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
        Configuration.GetSection("Authorization").Bind(_authOptions);
        Configuration.GetSection("SignalROptions").Bind(_signalROptions);
        _pathbase = Configuration["PathBase"];
    }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        // Add Azure Application Insights, if connection string is supplied
        string appInsights = Configuration["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(appInsights))
        {
            services.AddApplicationInsightsTelemetry();
        }

        var provider = Configuration["Database:Provider"];
        var connectionString = Configuration.GetConnectionString(provider);
        switch (provider)
        {
            case "InMemory":
                services.AddPooledDbContextFactory<BlueprintContext>((serviceProvider, optionsBuilder) => optionsBuilder
                    .AddInterceptors(serviceProvider.GetRequiredService<EventInterceptor>(), serviceProvider.GetRequiredService<SanitizerInterceptor>())
                    .UseInMemoryDatabase("api"));
                break;
            case "Sqlite":
                services.AddPooledDbContextFactory<BlueprintContext>((serviceProvider, optionsBuilder) => optionsBuilder
                   .AddInterceptors(serviceProvider.GetRequiredService<EventInterceptor>(), serviceProvider.GetRequiredService<SanitizerInterceptor>())
                   .UseConfiguredDatabase(Configuration))
                    .AddHealthChecks().AddSqlite(connectionString, tags: new[] { "ready", "live" });
                break;
            case "SqlServer":
                services.AddPooledDbContextFactory<BlueprintContext>((serviceProvider, optionsBuilder) => optionsBuilder
                    .AddInterceptors(serviceProvider.GetRequiredService<EventInterceptor>(), serviceProvider.GetRequiredService<SanitizerInterceptor>())
                    .UseConfiguredDatabase(Configuration))
                    .AddHealthChecks().AddSqlServer(connectionString, tags: new[] { "ready", "live" });
                break;
            case "PostgreSQL":
                services.AddPooledDbContextFactory<BlueprintContext>((serviceProvider, optionsBuilder) => optionsBuilder
                    .AddInterceptors(serviceProvider.GetRequiredService<EventInterceptor>(), serviceProvider.GetRequiredService<SanitizerInterceptor>())
                    .UseConfiguredDatabase(Configuration))
                    .AddHealthChecks().AddNpgSql(connectionString, tags: new[] { "ready", "live" });
                break;
        }

        services.AddOptions()
            .Configure<DatabaseOptions>(Configuration.GetSection("Database"))
            .AddScoped(config => config.GetService<IOptionsMonitor<DatabaseOptions>>().CurrentValue)

            .Configure<ClaimsTransformationOptions>(Configuration.GetSection("ClaimsTransformation"))
            .AddScoped(config => config.GetService<IOptionsMonitor<ClaimsTransformationOptions>>().CurrentValue)

            .Configure<SeedDataOptions>(Configuration.GetSection("SeedData"))
            .AddScoped(config => config.GetService<IOptionsMonitor<SeedDataOptions>>().CurrentValue);

        services
            .Configure<ClientOptions>(Configuration.GetSection("ClientSettings"))
            .AddScoped(config => config.GetService<IOptionsMonitor<ClientOptions>>().CurrentValue);

        services.AddScoped<IClaimsTransformation, AuthorizationClaimsTransformer>();
        services.AddScoped<IUserClaimsService, UserClaimsService>();

        services.AddCors(options => options.UseConfiguredCors(Configuration.GetSection("CorsPolicy")));

        services.AddScoped<BlueprintContextFactory>();
        services.AddScoped(sp => sp.GetRequiredService<BlueprintContextFactory>().CreateDbContext());

        services.AddSignalR(o => o.StatefulReconnectBufferSize = _signalROptions.StatefulReconnectBufferSizeBytes)
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.AddMvc(options =>
        {
            options.Filters.Add(typeof(ValidateModelStateFilter));
            options.Filters.Add(typeof(JsonExceptionFilter));

            // Require all scopes in authOptions
            var policyBuilder = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();
            Array.ForEach(_authOptions.AuthorizationScope.Split(' '), x => policyBuilder.RequireScope(x));

            var policy = policyBuilder.Build();
            options.Filters.Add(new AuthorizeFilter(policy));
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonNullableGuidConverter());
            options.JsonSerializerOptions.Converters.Add(new JsonDoubleConverter());
            options.JsonSerializerOptions.Converters.Add(new JsonIntegerConverter());
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

        services.AddSwagger(_authOptions);
        services.AddCiteApiClient();
        services.AddGalleryApiClient();
        services.AddPlayerApiClient();
        services.AddSteamfitterApiClient();

        JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = _authOptions.Authority;
            options.RequireHttpsMetadata = _authOptions.RequireHttpsMetadata;
            options.SaveToken = true;

            string[] validAudiences;
            if (_authOptions.ValidAudiences != null && _authOptions.ValidAudiences.Any())
            {
                validAudiences = _authOptions.ValidAudiences;
            }
            else
            {
                validAudiences = _authOptions.AuthorizationScope.Split(' ');
            }

            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateAudience = _authOptions.ValidateAudience,
                ValidAudiences = validAudiences
            };
        });

        services.AddRouting(options =>
        {
            options.LowercaseUrls = true;
        });

        services.AddMemoryCache();

        services.AddScoped<ICardService, CardService>();
        services.AddScoped<ICardTeamService, CardTeamService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<ICatalogInjectService, CatalogInjectService>();
        services.AddScoped<ICatalogUnitService, CatalogUnitService>();
        services.AddScoped<ICiteService, CiteService>();
        services.AddScoped<ICiteActionService, CiteActionService>();
        services.AddScoped<ICiteRoleService, CiteRoleService>();
        services.AddScoped<IDataFieldService, DataFieldService>();
        services.AddScoped<IDataOptionService, DataOptionService>();
        services.AddScoped<IDataValueService, DataValueService>();
        services.AddScoped<IScenarioEventService, ScenarioEventService>();
        services.AddScoped<IInjectService, InjectService>();
        services.AddScoped<IInjectTypeService, InjectTypeService>();
        services.AddScoped<IInvitationService, InvitationService>();
        services.AddScoped<IInjectTypeService, InjectTypeService>();
        services.AddScoped<IMselService, MselService>();
        services.AddScoped<IMselPageService, MselPageService>();
        services.AddScoped<IMselUnitService, MselUnitService>();
        services.AddScoped<IMoveService, MoveService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IPlayerApplicationService, PlayerApplicationService>();
        services.AddScoped<IPlayerApplicationTeamService, PlayerApplicationTeamService>();
        services.AddScoped<IPlayerService, PlayerService>();
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<ITeamUserService, TeamUserService>();
        services.AddScoped<IUnitService, UnitService>();
        services.AddScoped<IUnitUserService, UnitUserService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IUserPermissionService, UserPermissionService>();
        services.AddScoped<IUserMselRoleService, UserMselRoleService>();
        services.AddScoped<IUserTeamRoleService, UserTeamRoleService>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddScoped<IPrincipal>(p => p.GetService<IHttpContextAccessor>().HttpContext?.User);
        services.AddHttpClient();
        services.AddSingleton<IIntegrationQueue, IntegrationQueue>();
        services.AddHostedService<IntegrationService>();
        services.AddSingleton<IJoinQueue, JoinQueue>();
        services.AddHostedService<JoinService>();
        services.AddSingleton<IAddApplicationQueue, AddApplicationQueue>();
        services.AddHostedService<AddApplicationService>();

        ApplyPolicies(services);

        services.AddTransient<EventInterceptor>();
        services.AddTransient<SanitizerInterceptor>();
        services.AddAutoMapper(cfg =>
        {
            cfg.Internal().ForAllPropertyMaps(
                pm => pm.SourceType != null && Nullable.GetUnderlyingType(pm.SourceType) == pm.DestinationType,
                (pm, c) => c.MapFrom<object, object, object, object>(new IgnoreNullSourceValues(), pm.SourceMember.Name));
        }, typeof(Startup));
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Startup>());
        services
            .Configure<ResourceOwnerAuthorizationOptions>(Configuration.GetSection("ResourceOwnerAuthorization"))
            .AddScoped(config => config.GetService<IOptionsMonitor<ResourceOwnerAuthorizationOptions>>().CurrentValue);

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(typeof(Startup).Assembly));

        services.AddHtmlSanitizer(Configuration);
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        app.UsePathBase(_pathbase);
        app.UseRouting();
        app.UseCors("default");

        //move any querystring jwt to Auth bearer header
        app.Use(async (context, next) =>
        {
            if (string.IsNullOrWhiteSpace(context.Request.Headers["Authorization"])
                && context.Request.QueryString.HasValue)
            {
                string token = context.Request.QueryString.Value
                    .Substring(1)
                    .Split('&')
                    .SingleOrDefault(x => x.StartsWith("bearer="))?.Split('=')[1];

                if (!String.IsNullOrWhiteSpace(token))
                    context.Request.Headers.Append("Authorization", new[] { $"Bearer {token}" });
            }

            await next.Invoke();

        });

        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.RoutePrefix = _routePrefix;
            c.SwaggerEndpoint($"{_pathbase}/swagger/v1/swagger.json", "Blueprint v1");
            c.OAuthClientId(_authOptions.ClientId);
            c.OAuthClientSecret(_authOptions.ClientSecret);
            c.OAuthAppName(_authOptions.ClientName);
            c.OAuthUsePkce();
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<Hubs.MainHub>("/hubs/main", options =>
                {
                    options.AllowStatefulReconnects = _signalROptions.EnableStatefulReconnect;
                });
                endpoints.MapHealthChecks($"/{_routePrefix}/health/ready", new HealthCheckOptions()
                {
                    Predicate = (check) => check.Tags.Contains("ready"),
                });
                endpoints.MapHealthChecks($"/{_routePrefix}/health/live", new HealthCheckOptions()
                {
                    Predicate = (check) => check.Tags.Contains("live"),
                });
            }
        );

        app.UseHttpContext();
    }


    private void ApplyPolicies(IServiceCollection services)
    {
        services.AddAuthorizationPolicy(_authOptions);
    }
}