using Microsoft.EntityFrameworkCore;
using OnlineRadioStation.Data;
using OnlineRadioStation.Services;
using OnlineRadioStation.Domain;
using Microsoft.AspNetCore.StaticFiles; 

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ApplicationContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<IStationRepository, StationRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ITrackRepository, TrackRepository>();
builder.Services.AddScoped<IPlaybackQueueRepository, PlaybackQueueRepository>();
builder.Services.AddScoped<IDjStreamRepository, DjStreamRepository>();
builder.Services.AddScoped<ILikeDislikeRepository, LikeDislikeRepository>();
builder.Services.AddScoped<ISavedStationRepository, SavedStationRepository>();

builder.Services.AddScoped<IStationService, StationService>();
builder.Services.AddScoped<IUserService, UserService>();

builder.Services.AddTransient<IAudioConverter, FFmpegAdapter>();
builder.Services.AddTransient<StreamFactory, BitrateStreamFactory>();
builder.Services.AddScoped<IAudioProcessor, AudioProcessingFacade>();
builder.Services.AddScoped<ListeningStatsVisitor>();

builder.Services.AddSingleton<LiveStreamService>(provider => 
{
    var env = provider.GetRequiredService<IWebHostEnvironment>();
    return new LiveStreamService(env.WebRootPath);
});

builder.Services.AddSession();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationContext>();
        DbInitializer.Initialize(context);
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while seeding the database.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

var provider = new FileExtensionContentTypeProvider();
provider.Mappings[".m3u8"] = "application/vnd.apple.mpegurl"; 
provider.Mappings[".ts"] = "video/mp2t"; 

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider 
});

app.UseSession();
app.UseRouting();
app.UseAuthorization();
app.UseWebSockets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();