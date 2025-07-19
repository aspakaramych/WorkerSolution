using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace CodeExecutionService.Services;
public class RabbitMQPublisher : IHostedService, IAsyncDisposable
{
    private IConnection _connection;
    private IChannel _channel;
    private readonly string _hostname;
    private readonly string _queueName;

    public RabbitMQPublisher(IConfiguration configuration)
    {
        _hostname = configuration["RabbitMQ:HostName"] ?? "localhost";
        _queueName = configuration["RabbitMQ:RequestQueue"] ?? "code_execution_requests";
    }


    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory() { HostName = _hostname };
        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
        

        await _channel.QueueDeclareAsync(queue: _queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);
        Console.WriteLine($"RabbitMQPublisher started, connected to {_hostname}, publishing to queue {_queueName}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Shutting down RabbitMQPublisher...");
        await DisposeAsync(); 
    }

    public void Publish<T>(T message, string routingKey = "")
    {
        if (_channel == null || !_channel.IsOpen)
        {
            Console.Error.WriteLine("RabbitMQ channel is not open. Cannot publish message.");
            return;
        }

        var json = JsonSerializer.Serialize(message);
        var body = Encoding.UTF8.GetBytes(json);
        
        _channel.BasicPublishAsync(exchange: "", routingKey: routingKey, body: body);
        Console.WriteLine($"Published message to {routingKey}: {json}");
    }


    public async ValueTask DisposeAsync()
    {
        if (_channel != null && _channel.IsOpen)
        {
            await _channel.CloseAsync();
            _channel.Dispose();
        }

        if (_connection != null && _connection.IsOpen)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
        }
        _channel = null;
        _connection = null;
    }
}