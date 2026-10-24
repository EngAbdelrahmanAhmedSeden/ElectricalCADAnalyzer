using CircuitCAD.Models;
using CircuitCAD.Services;
using Microsoft.AspNetCore.Mvc;

namespace CircuitCAD.Controllers;

[ApiController]
[Route("api/circuit")]
public sealed class CircuitController : ControllerBase
{
    [HttpPost("analyze")]
    [RequestSizeLimit(1_000_000)]
    public ActionResult<AnalysisResponse> Analyze([FromBody] CircuitRequest request)
    {
        try
        {
            return Ok(CircuitAnalyzer.Analyze(request));
        }
        catch (GraphAnalysisException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }
}
