using BruteForceBackend;
using Microsoft.AspNetCore.Mvc;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy => policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("AllowReactApp"); 

// --- API ENDPOINTS ---

app.MapPost("/api/crack", async ([FromBody] HashRequest request, CancellationToken ct) =>
{
    Console.WriteLine($"[Hybrid] Starting crack for: {request.HashToCrack}");
    BruteForceStats.TotalChecks = 0;
    BruteForceStats.Speed = 0;
    BruteForceStats.CurrentLength = 0;

    var hybridSolver = new HybridDeHashing();
    
    // The hybrid solver handles the parallelism internally
    string result = await hybridSolver.SolveAsync(request, ct);

    return Results.Ok(new { Password = result });
});

app.MapGet("/api/progress", () => 
{
    return Results.Ok(new { 
        checks = BruteForceStats.TotalChecks, 
        speed = BruteForceStats.Speed,
        currentLength = BruteForceStats.CurrentLength
    });
});

app.Run();

// --- DEFINITIONS ---

public static class BruteForceStats 
{
    public static long TotalChecks = 0;
    public static double Speed = 0;
    public static int CurrentLength = 0;
}

internal record HashRequest(
    string HashToCrack, 
    int MinLength, 
    string PepperLocation, 
    bool UseNumbers, 
    bool UseSmallLetters, 
    bool UseBigLetters, 
    bool UseSpecialChars
);