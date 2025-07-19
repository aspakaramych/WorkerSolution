using System.Collections.Concurrent;
using CodeExecutionService.Host;
using CodeExecutionService.Models;
using CodeExecutionService.Services;
using Microsoft.Extensions.DependencyInjection; // Убедитесь, что этот using присутствует

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen();

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

app.MapControllers();

app.Run();