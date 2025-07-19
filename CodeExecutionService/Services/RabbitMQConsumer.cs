using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeExecutionService.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CodeExecutionService.Services;
public class RabbitMQConsumer : IHostedService, IAsyncDisposable
{
    private IConnection _connection;
    private IChannel _channel;
    private readonly string _hostname;
    private readonly string _queueName;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CodeExecutionResult>> _callbacks;

    public RabbitMQConsumer(IConfiguration configuration,
        ConcurrentDictionary<string, TaskCompletionSource<CodeExecutionResult>> callbacks)
    {

        _hostname = configuration["RabbitMQ:HostName"] ?? "localhost";
        _queueName = configuration["RabbitMQ:ResultQueue"] ?? "code_execution_results";
        _callbacks = callbacks;
    }


    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory() { HostName = _hostname };
        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
        
        await _channel.QueueDeclareAsync(queue: _queueName,
            durable: false,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        var eventConsumer = new AsyncEventingBasicConsumer(_channel);
        eventConsumer.ReceivedAsync += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            CodeExecutionResult result = null;
            try
            {
                result = JsonSerializer.Deserialize<CodeExecutionResult>(message);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Error deserializing message: {ex.Message}. Message: {message}");
                await _channel.BasicNackAsync(ea.DeliveryTag, false, false);
                return;
            }


            if (result != null && _callbacks.TryRemove(result.CorrelationId, out var tsc))
            {
                tsc.SetResult(result);
            }
            else if (result == null)
            {
                Console.Error.WriteLine($"Received null result or failed to deserialize for correlationId: {ea.BasicProperties.CorrelationId}. Message: {message}");
            }
            else
            {
                Console.WriteLine($"No pending TaskCompletionSource found for CorrelationId: {result.CorrelationId}");
            }

            await _channel.BasicAckAsync(ea.DeliveryTag, false);
        };
        

        await _channel.BasicConsumeAsync(queue: _queueName, autoAck: false, consumer: eventConsumer);
        Console.WriteLine($"RabbitMQConsumer started, connected to {_hostname}, consuming from queue {_queueName}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Shutting down RabbitMQConsumer...");
        await DisposeAsync();
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