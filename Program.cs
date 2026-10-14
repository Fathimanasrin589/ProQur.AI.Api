using ProQur.AI.Api.Interfaces;
using ProQur.AI.Api.Repositories;
using ProQur.AI.Api.Services;

var builder = WebApplication.CreateBuilder(args);
// Learning prototype: bind only to this computer and use fictional records.
builder.WebHost.UseUrls("http://127.0.0.1:5188");
builder.Services.AddControllers();
builder.Services.AddScoped<IPrReader, SqlPrReader>();
builder.Services.AddScoped<SqlPrApprovalReader>();
builder.Services.AddHttpClient<LocalModelClient>(client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:11434/");
    client.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddHttpClient("OllamaIntent", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:11434/");
    client.Timeout = TimeSpan.FromSeconds(120);
});
var app = builder.Build();
app.MapControllers();
app.Run();
