using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeExecutionService.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

public class CodeExecutionWorker : BackgroundService
{
    private IConnection _connection;
    private IChannel _channel;
    private readonly string _requestQueueName;
    private readonly string _resultQueueName;
    private readonly IConfiguration _configuration;
    private readonly string _rabbitMqHost;
    private readonly DockerClient _dockerClient; 
    private Timer _queueCheckTimer;

    public CodeExecutionWorker(IConfiguration configuration)
    {
        _configuration = configuration;
        _rabbitMqHost = _configuration["RabbitMQ:HostName"] ?? "localhost";
        _requestQueueName = _configuration["RabbitMQ:RequestQueue"] ?? "code_execution_requests";
        _resultQueueName = _configuration["RabbitMQ:ResultQueue"] ?? "code_execution_results";

        _dockerClient = new DockerClientConfiguration(
            new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
        Console.WriteLine("DockerClient initialized for Unix socket.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"CodeExecutionWorker starting. Connecting to RabbitMQ at {_rabbitMqHost}...");
        try
        {
            var factory = new ConnectionFactory() { HostName = _rabbitMqHost };
            Console.WriteLine($"RabbitMQ ConnectionFactory HostName set to: {factory.HostName}");

            factory.AutomaticRecoveryEnabled = true;
            factory.NetworkRecoveryInterval = TimeSpan.FromSeconds(10);

            _connection = await factory.CreateConnectionAsync(stoppingToken);
            _connection.ConnectionShutdownAsync += async (sender, reason) =>
            {
                Console.WriteLine($"RabbitMQ Connection Shutdown: {reason.Cause} - {reason.ReplyText}");
            };
            _connection.CallbackExceptionAsync += async (sender, ea) =>
            {
                Console.Error.WriteLine($"RabbitMQ Callback Exception: {ea.Exception.Message}");
            };

            _channel = await _connection.CreateChannelAsync();
            _channel.CallbackExceptionAsync += async (sender, ea) =>
            {
                Console.Error.WriteLine($"RabbitMQ Channel Callback Exception: {ea.Exception.Message}");
            };

            Console.WriteLine("RabbitMQ connection and channel established.");

            await _channel.QueueDeclareAsync(queue: _requestQueueName,
                durable: false,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: stoppingToken);
            Console.WriteLine($"Declared request queue: {_requestQueueName}");

            await _channel.QueueDeclareAsync(queue: _resultQueueName,
                durable: false,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: stoppingToken);
            Console.WriteLine($"Declared result queue: {_resultQueueName}");

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                Console.WriteLine($" [x] Message received. DeliveryTag: {ea.DeliveryTag}");
                var body = ea.Body.ToArray();
                string message = Encoding.UTF8.GetString(body);
                CodeExecutionRequest request = null;

                try
                {
                    request = JsonSerializer.Deserialize<CodeExecutionRequest>(message);
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine($"Error deserializing message: {ex.Message}. Message: {message}");
                    await _channel.BasicNackAsync(ea.DeliveryTag, false, false);
                    return;
                }

                if (request != null)
                {
                    Console.WriteLine(
                        $" [x] Received request for {request.Language} code with CorrelationId: {request.CorrelationId}");
                    Console.WriteLine($"Code content (first 100 chars): {request.Code.Substring(0, Math.Min(request.Code.Length, 100))}");

                    CodeExecutionResult result;
                    try
                    {
                        result = await ExecuteCodeInDocker(request.Code, request.Language);
                        result.CorrelationId = request.CorrelationId;
                        Console.WriteLine($"Code execution finished for CorrelationId: {request.CorrelationId}");
                        Console.WriteLine($"Output: {result.Output}");
                        Console.WriteLine($"Error: {result.Error}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error during code execution for CorrelationId {request.CorrelationId}: {ex.Message}");
                        result = new CodeExecutionResult
                        {
                            CorrelationId = request.CorrelationId,
                            Error = $"Internal worker error: {ex.Message}",
                            Output = ""
                        };
                    }

                    var jsonResult = JsonSerializer.Serialize(result);
                    var responseBody = Encoding.UTF8.GetBytes(jsonResult);

                    try
                    {
                        await _channel.BasicPublishAsync(exchange: "",
                            routingKey: _resultQueueName,
                            body: responseBody);
                        await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        Console.WriteLine($" [x] Sent result for CorrelationId: {request.CorrelationId} to {_resultQueueName}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error publishing result or acknowledging message for CorrelationId {request.CorrelationId}: {ex.Message}");
                        await _channel.BasicNackAsync(ea.DeliveryTag, false, true);
                    }
                }
                else
                {
                    Console.Error.WriteLine($" [x] Received null request after deserialization. Message: {message}");
                    await _channel.BasicNackAsync(ea.DeliveryTag, false, false);
                }
            };

            await _channel.BasicConsumeAsync(queue: _requestQueueName,
                autoAck: false,
                consumer: consumer);
            Console.WriteLine($"Started consuming from queue: {_requestQueueName}");

            _queueCheckTimer = new Timer(async _ => await CheckQueueStatus(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("CodeExecutionWorker stopping due to cancellation.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CodeExecutionWorker encountered a critical error: {ex.Message}");
        }
    }

    private async Task CheckQueueStatus()
    {
        try
        {
            if (_channel != null && _channel.IsOpen)
            {
                var result = await _channel.QueueDeclarePassiveAsync(_requestQueueName);
                Console.WriteLine($"Queue '{_requestQueueName}' status: {result.MessageCount} messages ready, {result.ConsumerCount} consumers.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to check queue status: {ex.Message}");
        }
    }

    private async Task<CodeExecutionResult> ExecuteCodeInDocker(string code, string language)
    {
        string output = "";
        string error = "";
        string containerId = null;

        try
        {
            string dockerImage = "";
            string codeFileName = "";
            string executionCommand = "";
            string containerWorkingDir = "/app";

            switch (language.ToLower())
            {
                case "python":
                    dockerImage = "python:3.9-slim-buster";
                    codeFileName = "script.py";
                    executionCommand = $"python {containerWorkingDir}/{codeFileName}";
                    break;
                case "csharp":
                    dockerImage = "mcr.microsoft.com/dotnet/sdk:8.0";
                    codeFileName = "Program.cs";
                    containerWorkingDir = "/app/MyCSharpProject";
                    executionCommand = "dotnet new console --force -o . && " +
                                       $"mv ../{codeFileName} ./{codeFileName} && " +
                                       "dotnet build && " +
                                       "dotnet run";
                    break;
                case "javascript":
                    dockerImage = "node:18-slim";
                    codeFileName = "script.js";
                    executionCommand = $"node {containerWorkingDir}/{codeFileName}";
                    break;
                default:
                    error = "Unsupported language.";
                    Console.Error.WriteLine($"Unsupported language requested: {language}");
                    return new CodeExecutionResult { Output = output, Error = error };
            }


            await EnsureDockerImageExists(dockerImage);


            var createContainerParameters = new CreateContainerParameters
            {
                Image = dockerImage,
                AttachStdout = true,
                AttachStderr = true,
                OpenStdin = false,
                WorkingDir = "/app", 
                HostConfig = new HostConfig
                {
                    NetworkMode = "none",
                },
                Cmd = new List<string> { "tail", "-f", "/dev/null" } 
            };

            var container = await _dockerClient.Containers.CreateContainerAsync(createContainerParameters);
            containerId = container.ID;
            Console.WriteLine($"Created Docker container: {containerId}");

            await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            Console.WriteLine($"Started Docker container: {containerId}");

            string writeCodeCommand;
            if (language.ToLower() == "csharp")
            {
                writeCodeCommand = $"mkdir -p {containerWorkingDir} && printf \"%s\" \"{code.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")}\" > {containerWorkingDir}/{codeFileName}";
            }
            else
            {
                writeCodeCommand = $"printf \"%s\" \"{code.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "")}\" > {containerWorkingDir}/{codeFileName}";
            }

            var execCreateResponseWrite = await _dockerClient.Exec.ExecCreateContainerAsync(
                containerId,
                new ContainerExecCreateParameters
                {
                    AttachStdout = true,
                    AttachStderr = true,
                    Cmd = new List<string> { "sh", "-c", writeCodeCommand },
                    WorkingDir = "/app" 
                },
                CancellationToken.None); 

            using (var streamWrite = await _dockerClient.Exec.StartAndAttachContainerExecAsync(
                execCreateResponseWrite.ID,
                false, 
                CancellationToken.None))
            {

                (string stdOut, string stdErr) = await streamWrite.ReadOutputToEndAsync(CancellationToken.None);

                var inspectExecWrite = await _dockerClient.Exec.InspectContainerExecAsync(execCreateResponseWrite.ID, CancellationToken.None);
                if (inspectExecWrite.ExitCode != 0 || !string.IsNullOrEmpty(stdErr))
                {
                    Console.Error.WriteLine($"Error writing code to container (Exec ID: {execCreateResponseWrite.ID}): {stdErr} (Exit Code: {inspectExecWrite.ExitCode})");
                    return new CodeExecutionResult { Output = "", Error = $"Failed to write code to container: {stdErr} (Exit Code: {inspectExecWrite.ExitCode})" };
                }
            }
            Console.WriteLine($"Code written to {containerWorkingDir}/{codeFileName} in container {containerId}.");



            var execCreateResponseExecute = await _dockerClient.Exec.ExecCreateContainerAsync(
                containerId,
                new ContainerExecCreateParameters
                {
                    AttachStdout = true,
                    AttachStderr = true,
                    Cmd = new List<string> { "sh", "-c", executionCommand },
                    WorkingDir = containerWorkingDir 
                },
                CancellationToken.None); 

            using (var streamExecute = await _dockerClient.Exec.StartAndAttachContainerExecAsync(
                execCreateResponseExecute.ID,
                false, 
                CancellationToken.None))
            {
                (output, error) = await streamExecute.ReadOutputToEndAsync(CancellationToken.None);
            }

            var inspectExecResponse = await _dockerClient.Exec.InspectContainerExecAsync(execCreateResponseExecute.ID, CancellationToken.None);
            long? statusCode = inspectExecResponse.ExitCode;

            if (statusCode != 0 && string.IsNullOrEmpty(error))
            {
                error = $"Code execution failed with exit code {statusCode}. Output: {output}";
            }
        }
        catch (DockerApiException dockerEx)
        {
            error = $"Docker API error: {dockerEx.Message} (StatusCode: {dockerEx.StatusCode})";
            Console.Error.WriteLine($"Docker API Exception: {error}");
        }
        catch (Exception ex)
        {
            error = $"Internal server error during Docker execution: {ex.Message}";
            Console.Error.WriteLine($"Exception in ExecuteCodeInDocker: {ex.Message}");
        }
        finally
        {
            if (containerId != null)
            {
                try
                {
                    await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
                    Console.WriteLine($"Removed Docker container: {containerId}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error removing Docker container {containerId}: {ex.Message}");
                }
            }
        }

        return new CodeExecutionResult { Output = output, Error = error };
    }

    private async Task EnsureDockerImageExists(string imageName)
    {
        try
        {
            var images = await _dockerClient.Images.ListImagesAsync(new ImagesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    { "reference", new Dictionary<string, bool> { { imageName, true } } }
                }
            });

            if (images.Count == 0)
            {
                Console.WriteLine($"Docker image '{imageName}' not found locally. Pulling...");
                await _dockerClient.Images.CreateImageAsync(
                    new ImagesCreateParameters
                    {
                        FromImage = imageName.Split(':')[0],
                        Tag = imageName.Contains(":") ? imageName.Split(':')[1] : "latest"
                    },
                    null, 
                    new Progress<JSONMessage>(), 
                    CancellationToken.None 
                );
                Console.WriteLine($"Docker image '{imageName}' pulled successfully.");
            }
            else
            {
                Console.WriteLine($"Docker image '{imageName}' found locally.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error ensuring Docker image '{imageName}' exists: {ex.Message}");
            throw; 
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("CodeExecutionWorker is stopping.");
        _queueCheckTimer?.Dispose();
        if (_channel != null && _channel.IsOpen)
        {
            await _channel.CloseAsync();
            _channel.Dispose();
            Console.WriteLine("RabbitMQ channel closed.");
        }

        if (_connection != null && _connection.IsOpen)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
            Console.WriteLine("RabbitMQ connection closed.");
        }

        await base.StopAsync(cancellationToken);
    }
}