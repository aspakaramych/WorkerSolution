using System.Collections.Concurrent;
using CodeExecutionService.Models;
using CodeExecutionService.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("[controller]")]
public class CodeExecutionController : ControllerBase
{
    private readonly RabbitMQPublisher _publisher;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CodeExecutionResult>> _pendingRequests;

    public CodeExecutionController(RabbitMQPublisher publisher, ConcurrentDictionary<string, TaskCompletionSource<CodeExecutionResult>> pendingRequests)
    {
        _publisher = publisher;
        _pendingRequests = pendingRequests;
    }
    

    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteCode([FromBody] CodeExecutionRequest request)
    {
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrWhiteSpace(request.Language))
        {
            return BadRequest("Code and language are required");
        }
        request.CorrelationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<CodeExecutionResult>();
        _pendingRequests.TryAdd(request.CorrelationId, tcs);

        try
        {
            try
            {
                _publisher.Publish(request, "code_execution_requests");
                Console.WriteLine($"Published message with CorrelationId: {request.CorrelationId} to queue: code_execution_requests");
            }
            catch (Exception publishEx)
            {
                Console.Error.WriteLine($"Error publishing message to RabbitMQ: {publishEx.Message}");
                _pendingRequests.TryRemove(request.CorrelationId, out _);
                return StatusCode(500, $"Failed to publish code execution request: {publishEx.Message}");
            }
            
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var registration = cts.Token.Register(() =>
            {
                if (_pendingRequests.TryRemove(request.CorrelationId, out var timedOutTcs))
                {
                    timedOutTcs.TrySetCanceled();
                }
            });
            
            var result = await tcs.Task.WaitAsync(cts.Token);
            _pendingRequests.TryRemove(request.CorrelationId, out _);
            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(request.CorrelationId, out _);
            return StatusCode(504, "Code execution request was cancelled");
        }
        catch (Exception e)
        {
            _pendingRequests.TryRemove(request.CorrelationId, out _);
            return StatusCode(500, e.Message);
        }
    }
}
