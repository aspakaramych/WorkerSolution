namespace CodeExecutionService.Models;

public class CodeExecutionResult
{
    public string Output { get; set; }
    public string Error { get; set; }
    public string CorrelationId { get; set; }
}