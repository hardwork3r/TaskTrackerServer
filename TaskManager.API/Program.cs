using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Serilog;
using Serilog.Events;
using TaskManager.API.Middleware;
using TaskManager.API.Services;
using TaskManager.Application;
using TaskManager.Application.Common.Interfaces;
using TaskManager.Infrastructure;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "TaskManager")
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .WriteTo.File(
        path: "logs/taskmanager-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: 30,
        shared: true
    )
    .CreateLogger();

try
{
    Log.Information("Starting TaskManager API...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    builder.Configuration.AddEnvironmentVariables();

    var mongoConnectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING");
    var mongoDatabaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE_NAME");

    if (!string.IsNullOrEmpty(mongoConnectionString))
    {
        builder.Configuration["MongoDB:ConnectionString"] = mongoConnectionString;
        Log.Information("MongoDB connection string loaded from environment variable");
    }

    if (!string.IsNullOrEmpty(mongoDatabaseName))
    {
        builder.Configuration["MongoDB:DatabaseName"] = mongoDatabaseName;
    }

    var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET_KEY")
        ?? builder.Configuration["JWT:SecretKey"]
        ?? "your-secret-key-change-in-production";

    var corsOrigins = Environment.GetEnvironmentVariable("CORS_ORIGINS")?.Split(',')
        ?? builder.Configuration["CORS:Origins"]?.Split(',')
        ?? new[] { "*" };

    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddApplication();

    // API Services
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

    // JWT Authentication
    var jwtKey = Encoding.UTF8.GetBytes(jwtSecret);

    builder.Services.AddAuthentication(x =>
    {
        x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(x =>
    {
        x.RequireHttpsMetadata = false;
        x.SaveToken = true;
        x.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

    builder.Services.AddAuthorization();

    // CORS
    var corsOrigins = builder.Configuration["CORS:Origins"]?.Split(',') ?? new[] { "*" };
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            if (corsOrigins.Length == 1 && corsOrigins[0] == "*")
            {
                policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            }
            else
            {
                policy.WithOrigins(corsOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            }
        });
    });

    // Controllers & Swagger
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    // Serilog Request Logging
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000}ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
            diagnosticContext.Set("ClientIP", httpContext.Connection.RemoteIpAddress);
        };
    });

    // Swagger
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Task Manager API v1");
        c.RoutePrefix = string.Empty;
    });

    // Middleware
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();

    // Health Check
    app.MapGet("/health", () =>
    {
        Log.Debug("Health check requested");
        return Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            environment = app.Environment.EnvironmentName
        });
    });

    app.MapControllers();

    // Configure URLs
    var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";

    if (app.Environment.IsDevelopment())
    {
        app.Urls.Add($"http://localhost:{port}");
        Log.Information("Server running at http://localhost:{Port}", port);
        Log.Information("Swagger UI: http://localhost:{Port}/swagger", port);
    }
    else
    {
        app.Urls.Add($"http://0.0.0.0:{port}");
        Log.Information("Production server running on port {Port}", port);
    }

    Log.Information("Application started successfully");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.Information("Application shutting down...");
    Log.CloseAndFlush();
}
