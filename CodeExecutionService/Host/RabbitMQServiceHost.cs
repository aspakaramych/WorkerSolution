namespace CodeExecutionService.Host;


public class RabbitMQServiceHost : IHostedService
{
    public RabbitMQServiceHost()
    {
        
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("RabbitMQ Service Host started (coordinating other RabbitMQ services).");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("RabbitMQ Service Host is shutting down.");
        return Task.CompletedTask;
    }
}