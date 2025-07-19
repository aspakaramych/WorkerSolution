namespace CodeExecutionService.Models;

public class CodeExecutionRequest
{
    public string Code { get; set; }
    public string Language { get; set; }
    public string CorrelationId { get; set; }
}