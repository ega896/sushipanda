﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using API.Filter;
using API.Options;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using AutoMapper;
using Domain.Models;
using Domain.Options;
using Emails;
using FluentValidation.AspNetCore;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using Infrastructure.Files;
using Infrastructure.FileSystem;
using Infrastructure.Hubs;
using Infrastructure.Notifications;
using Infrastructure.SmtpMailing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using NSwag;
using NSwag.Generation.Processors.Security;
using Persistence;
using Repositories;
using Repositories.Interfaces;
using Services;
using Services.Events;
using Services.Events.EventHandling;
using Services.Events.EventHandling.Interfaces;
using Services.Events.Models;
using Services.Identity;
using Services.Interfaces;
using Services.MappingProfiles;
using Services.Validators;

namespace API
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            DatabasesConfiguration(services);

            MailingConfiguration(services);

            EventsConfigurations(services);

            UserManagementAndAuthConfigurations(services);

            ProjectServicesConfiguration(services);

            services.AddAutoMapper(typeof(UserMappingProfile).Assembly);

            services.AddSwaggerDocument(options =>
            {
                options.AddSecurity("JWT", Enumerable.Empty<string>(), new OpenApiSecurityScheme
                {
                    Type = OpenApiSecuritySchemeType.ApiKey,
                    Name = "Authorization",
                    In = OpenApiSecurityApiKeyLocation.Header,
                    Description = "Type into the textbox: Bearer {your JWT token}."
                });

                options.OperationProcessors.Add(new AspNetCoreOperationSecurityScopeProcessor("JWT"));
            });

            services.AddHangfire(configuration => configuration
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(Configuration.GetConnectionString("Hangfire"), new SqlServerStorageOptions
                {
                    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                    QueuePollInterval = TimeSpan.FromMinutes(5),  // Change to TimeSpan.FromMinutes(5) while debugging not hangfire
                    UseRecommendedIsolationLevel = true,
                    UsePageLocksOnDequeue = true,
                    DisableGlobalLocks = true
                }));
            services.AddHangfireServer();

            services.AddCors(o => o.AddPolicy("CorsPolicy", builder => {
                builder
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials()
                    .WithOrigins("http://localhost:3000");
            }));

            services.AddSignalR();

            services.AddMvc(options =>
            {
                options.Filters.Add(typeof(ExceptionFilter));
            })
            .AddFluentValidation(fv => fv.RegisterValidatorsFromAssemblyContaining<UserDtoValidator>())
            .SetCompatibilityVersion(CompatibilityVersion.Version_2_2);

            return ConfigureAutofac(services);
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
            }

            var cachePeriod = env.IsDevelopment() ? "600" : "604800";
            if (!Directory.Exists(Path.Combine(Configuration["FileStorageFolderUrl"], "files")))
            {
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "files"));
            }

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(
                    Path.Combine(Directory.GetCurrentDirectory(), "files")),
                RequestPath = "/files"
            });

            app.UseHttpsRedirection();
            app.UseCors("CorsPolicy");

            app.UseAuthentication();

            app.UseOpenApi();
            app.UseSwaggerUi3();

            var hangfireOptions = new DashboardOptions
            {
                DisplayStorageConnectionString = true,
                Authorization = env.IsDevelopment() ? new IDashboardAuthorizationFilter[0] :
                    new IDashboardAuthorizationFilter[] { new HangfireAuthorizationFilter() }
            };

            app.UseHangfireDashboard("/hangfire", hangfireOptions);

            app.UseSignalR(route =>
            {
                route.MapHub<NotificationHub>("/hub");
            });

            app.UseMvc();
        }

        private static void ProjectServicesConfiguration(IServiceCollection services)
        {
            services.AddTransient<IUsersService, UsersService>();
            services.AddTransient<IAuthService, AuthService>();
            services.AddTransient<IRefreshTokenService, RefreshTokenService>();

            services.AddTransient<IDishesService, DishesService>();
            services.AddTransient<IOrdersService, OrdersService>();

            services.AddTransient<IFileSystemService, FileSystemSystemService>();
            services.AddTransient<IFileService, FileService>();
        }

        private void MailingConfiguration(IServiceCollection services)
        {
            services.AddTransient<IMailSenderService, MailSenderService>();
            services.Configure<SmtpConfiguration>(Configuration.GetSection("Smtp"));
            services.AddSingleton<SmtpConfiguration>();
            services.AddTransient<IRazorViewToStringRenderer, RazorViewToStringRenderer>();
        }

        private IServiceProvider ConfigureAutofac(IServiceCollection services)
        {
            var builder = new ContainerBuilder();
            builder.Populate(services);

            builder.RegisterGeneric(typeof(RepositorySql<>)).Named("sql", typeof(IRepository<>)).InstancePerDependency();
            builder.RegisterGeneric(typeof(RepositoryRedis<>)).Named("redis", typeof(IRepository<>)).InstancePerDependency();

            builder.RegisterType<UnitOfWorkSql>().Keyed<IUnitOfWork>("sql").InstancePerDependency();
            builder.RegisterType<UnitOfWorkRedis>().Keyed<IUnitOfWork>("redis").InstancePerDependency();

            var container = builder.Build();
            return container.Resolve<IServiceProvider>();
        }

        private static void EventsConfigurations(IServiceCollection services)
        {
            services.AddTransient<IEventsManager, EventsManager>();
            services.AddTransient<IEventHandlerFactory, EventHandlerFactory>();
            services.AddTransient<IEventHandlerManager, EventHandlerManager>();
            services.AddTransient<IEventHandler<UserRegisteredEvent>, UserRegisteredEventHandler>();
            services.AddTransient<IEventHandler<OrderCreatedEvent>, OrderCreatedEventHandler>();
            services.AddTransient<INotificationService, NotificationService>();
        }

        private void DatabasesConfiguration(IServiceCollection services)
        {
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(Configuration.GetConnectionString("Mssql"));
            });

            services.Configure<RedisOptions>(options =>
            {
                options.ConnectionString = Configuration.GetConnectionString("Redis");
            });

            services.AddTransient<DbContext, ApplicationDbContext>();
            services.AddTransient<RedisDbContext, RedisDbContext>();
        }

        private void UserManagementAndAuthConfigurations(IServiceCollection services)
        {
            services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequiredLength = 6;
                options.Password.RequiredUniqueChars = 0;
            });

            services.Configure<DataProtectionTokenProviderOptions>(options =>
            {
                options.TokenLifespan = TimeSpan.FromHours(3);
            });

            var sKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(Configuration["JwtOptions:SecretKey"]));
            services.Configure<JwtOptions>(options =>
            {
                options.Issuer = Configuration["JwtOptions:Issuer"];
                options.Audience = Configuration["JwtOptions:Audience"];
                options.SigningCredentials = sKey;
            });
            services.AddSingleton<JwtOptions>();

            services.Configure<GoogleAuthOptions>(options =>
            {
                options.ClientId = Configuration["Auth:Google:ClientId"];
                options.ClientSecret = Configuration["Auth:Google:ClientSecret"];
            });
            services.AddSingleton<GoogleAuthOptions>();

            services.AddIdentityCore<User>().AddDefaultTokenProviders();
            services.AddScoped<IUserStore<User>, UserStore>();

            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = Configuration["JwtOptions:Issuer"],
                        ValidateAudience = true,
                        ValidAudience = Configuration["JwtOptions:Audience"],
                        ValidateLifetime = true,
                        IssuerSigningKey = sKey,
                        ValidateIssuerSigningKey = true,
                        ClockSkew = TimeSpan.Zero
                    };
                });

            services.AddAuthorization();
        }
    }
}