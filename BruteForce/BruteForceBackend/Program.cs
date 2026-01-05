using BruteForceBackend;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// --- CORS ---

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy => policy.WithOrigins("http://localhost:5173")
                        .AllowAnyMethod()
                        .AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("AllowReactApp");

// --- API ENDPOINTS ---

app.MapPost("/api/crack", async ([FromBody] HashRequest request, CancellationToken ct) =>
{
    Console.WriteLine($"Starting crack for: {request.HashToCrack}");

    var solver = new DeHashing { Hash = request.HashToCrack };
    
    try 
    {
        string result = await Task.Run(() => solver.DeHash(request.MaxLength, ct), ct);    
        return Results.Ok(new { Password = result });
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("User cancelled the operation.");
        return Results.NoContent();
    }
});

app.Run();

internal record HashRequest(string HashToCrack, int MaxLength);