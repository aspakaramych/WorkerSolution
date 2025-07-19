using System.Text;
using System.Text.Json;
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
                        _channel.BasicPublishAsync(exchange: "",
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
            Console.Error.WriteLine($"Error checking queue status: {ex.Message}");
        }
    }

    private async Task<CodeExecutionResult> ExecuteCodeInDocker(string code, string language)
    {
        string output = "";
        string error = "";
        string containerId = null;
        string tempDir = null;

        try
        {
            string dockerImage = "";
            List<string> cmdArgs = new List<string>(); 
            string entrypoint = null; 
            string containerWorkingDir = "/app"; 

   
            switch (language.ToLower())
            {
                case "python":
                    dockerImage = "python:3.9-slim-buster";
                    tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);
                    string pythonFilePath = Path.Combine(tempDir, "script.py");
                    await File.WriteAllTextAsync(pythonFilePath, code);
                    
                    entrypoint = "bash"; 
                    cmdArgs.Add("-c"); 
                    cmdArgs.Add($"ls -l {containerWorkingDir} && python {Path.Combine(containerWorkingDir, "script.py")}"); 
                    break;
                case "csharp":
                    dockerImage = "mcr.microsoft.com/dotnet/sdk:8.0";
                    tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);
                    string projectDir = Path.Combine(tempDir, "MyCSharpProject");
                    Directory.CreateDirectory(projectDir);
                    string programFilePath = Path.Combine(projectDir, "Program.cs");
                    await File.WriteAllTextAsync(programFilePath, code);
                    await File.WriteAllTextAsync(Path.Combine(projectDir, "MyCSharpProject.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>");
                    
                    entrypoint = "bash"; 
                    containerWorkingDir = "/app/MyCSharpProject"; 
                    cmdArgs.Add("-c");
                    cmdArgs.Add("dotnet new console --force -o . && cp Program.cs . && dotnet build && dotnet run");
                    break;
                case "javascript":
                    dockerImage = "node:18-slim";
                    tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);
                    string jsFilePath = Path.Combine(tempDir, "script.js");
                    await File.WriteAllTextAsync(jsFilePath, code);
                    
                    entrypoint = "node"; // Entrypoint для Node.js
                    cmdArgs.Add(Path.Combine(containerWorkingDir, "script.js")); 
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
                WorkingDir = containerWorkingDir,
                HostConfig = new HostConfig
                {
                    NetworkMode = "none",
                    Binds = tempDir != null ? new List<string> { $"{tempDir}:{containerWorkingDir}" } : null 
                },
                Entrypoint = entrypoint != null ? new List<string> { entrypoint } : null, 
                Cmd = cmdArgs 
            };


            var container = await _dockerClient.Containers.CreateContainerAsync(createContainerParameters);
            containerId = container.ID;
            Console.WriteLine($"Created Docker container: {containerId}");

            await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            Console.WriteLine($"Started Docker container: {containerId}");

            var waitResult = await _dockerClient.Containers.WaitContainerAsync(containerId, CancellationToken.None);
            Console.WriteLine($"Container {containerId} exited with status code: {waitResult.StatusCode}");

            var logs = await _dockerClient.Containers.GetContainerLogsAsync(containerId, new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true
            });

            using (var reader = new StreamReader(logs))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line.StartsWith("stdout:"))
                    {
                        output += line.Substring("stdout:".Length) + Environment.NewLine;
                    }
                    else if (line.StartsWith("stderr:"))
                    {
                        error += line.Substring("stderr:".Length) + Environment.NewLine;
                    }
                    else
                    {
                        output += line + Environment.NewLine;
                    }
                }
            }

            if (waitResult.StatusCode != 0 && string.IsNullOrEmpty(error))
            {
                error = $"Code execution failed with exit code {waitResult.StatusCode}. Output: {output}";
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

            if (tempDir != null && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                    Console.WriteLine($"Deleted temporary directory: {tempDir}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error deleting temporary directory {tempDir}: {ex.Message}");
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