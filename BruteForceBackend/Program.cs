using BruteForceBackend;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// 1. ADD SERVICE (Must happen before Build)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy => policy
            .WithOrigins("http://localhost:5173") // Ensure this matches your React URL exactly (no trailing slash)
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();

// 2. USE MIDDLEWARE (Crucial Step: MUST BE BEFORE MapPost/MapGet)
app.UseCors("AllowReactApp"); 

// --- API ENDPOINTS ---

app.MapPost("/api/crack", async ([FromBody] HashRequest request, CancellationToken ct) =>
{
    Console.WriteLine($"Starting crack for: {request.HashToCrack}");
    
    BruteForceStats.TotalChecks = 0;
    BruteForceStats.Speed = 0;
    BruteForceStats.CurrentLength = 0;

    var solver = new DeHashing 
    { 
        Hash = request.HashToCrack,
        PepperLocation = request.PepperLocation,
        UseNumbers = request.UseNumbers,
        UseSmallLetters = request.UseSmallLetters,
        UseBigLetters = request.UseBigLetters,
        UseSpecialChars = request.UseSpecialChars
    };

    string result = await Task.Run(() => solver.DeHash(request.MinLength, ct), ct);
    
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