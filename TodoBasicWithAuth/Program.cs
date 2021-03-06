using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Todos
{
    class Program
    {
        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var jwtSettings = JwtSettings.FromConfiguration(builder.Configuration);

            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("admin", policy => policy.RequireClaim("can_delete", "true"));
                options.AddPolicy("user", policy => policy.RequireClaim("can_view", "true"));
            });

            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options => options.TokenValidationParameters = jwtSettings.TokenValidationParameters);

            var userService = new UserService();

            var app = builder.Build();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapPost("/api/auth/token", context => GenerateTokenAsync(userService, jwtSettings, context));

            app.MapGet("/api/todos", GetAllAsync).RequireAuthorization();
            app.MapGet("/api/todos/{id}", GetAsync).RequireAuthorization("user");
            app.MapPost("/api/todos", PostAsync).RequireAuthorization();
            app.MapDelete("/api/todos/{id}", DeleteAsync).RequireAuthorization("admin");

            await app.RunAsync();
        }

        private static async Task GenerateTokenAsync(UserService userService, JwtSettings jwtSettings, HttpContext context)
        {
            var userInfo = await JsonSerializer.DeserializeAsync<UserInfo>(context.Request.Body, _options);

            bool isValidUser = userService.IsValid(userInfo?.UserName, userInfo?.Password);
            if (!isValidUser)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var claims = userService.GetUserClaims(userInfo.UserName).Select(name => new Claim(name, "true"));

            var key = new SymmetricSecurityKey(jwtSettings.Key);
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: jwtSettings.Issuer,
                audience: jwtSettings.Audience,
                claims: claims,
                expires: DateTime.Now.AddMinutes(30),
                signingCredentials: creds);

            await JsonSerializer.SerializeAsync(context.Response.Body, new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token)
            });
        }

        static async Task GetAllAsync(HttpContext context)
        {
            using var db = new TodoDbContext();
            var todos = await db.Todos.ToListAsync();

            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, todos, _options);
        }

        static async Task GetAsync(HttpContext context)
        {
            var id = (string)context.Request.RouteValues["id"];
            if (id == null || !long.TryParse(id, out var todoId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var db = new TodoDbContext();
            var todo = await db.Todos.FindAsync(todoId);
            if (todo == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, todo, _options);
        }

        static async Task PostAsync(HttpContext context)
        {
            var todo = await JsonSerializer.DeserializeAsync<Todo>(context.Request.Body, _options);

            using var db = new TodoDbContext();
            await db.Todos.AddAsync(todo);
            await db.SaveChangesAsync();

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        static async Task DeleteAsync(HttpContext context)
        {
            var id = (string)context.Request.RouteValues["id"];
            if (id == null || !long.TryParse(id, out var todoId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var db = new TodoDbContext();
            var todo = await db.Todos.FindAsync(todoId);
            if (todo == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            db.Todos.Remove(todo);
            await db.SaveChangesAsync();

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
    }
}
