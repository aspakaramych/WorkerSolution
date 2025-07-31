using System.Collections.Concurrent;
using CodeExecutionService.Host;
using CodeExecutionService.Models;
using CodeExecutionService.Services;


var builder = WebApplication.CreateBuilder(args);


builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen();

builder.Services.AddCors(opt =>
{
    opt.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<CodeExecutionResult>>();
builder.Services.AddSingleton(pendingRequests);


builder.Services.AddSingleton<RabbitMQPublisher>();

builder.Services.AddHostedService<RabbitMQPublisher>(sp => sp.GetRequiredService<RabbitMQPublisher>());

builder.Services.AddSingleton<RabbitMQConsumer>();

builder.Services.AddHostedService<RabbitMQConsumer>(sp => sp.GetRequiredService<RabbitMQConsumer>());
builder.Services.AddSingleton<RabbitMQServiceHost>(); 
builder.Services.AddHostedService<RabbitMQServiceHost>(sp => sp.GetRequiredService<RabbitMQServiceHost>());


var app = builder.Build();


app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();

app.MapControllers();

app.Run();