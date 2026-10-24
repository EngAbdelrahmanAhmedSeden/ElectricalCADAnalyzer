using System.ComponentModel.DataAnnotations;

namespace CircuitCAD.Models;

public sealed class CircuitRequest
{
    public List<string> Nodes { get; set; } = [];
    public List<BranchInput> Branches { get; set; } = [];
    public string ReferenceNode { get; set; } = string.Empty;
    public List<string>? TreeBranchIds { get; set; }
}

public sealed class BranchInput
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public double Impedance { get; set; }
    public double VoltageSource { get; set; }
    public double CurrentSource { get; set; }
}

public sealed class AnalysisResponse
{
    public required CircuitSummary Summary { get; init; }
    public required MatrixDto Incidence { get; init; }
    public required MatrixDto ReducedIncidence { get; init; }
    public required MatrixDto TieSet { get; init; }
    public required MatrixDto CutSet { get; init; }
    public required List<BranchResult> BranchResults { get; init; }
    public required List<NodeResult> NodeResults { get; init; }
    public required List<NamedValue> LoopCurrents { get; init; }
    public required List<NamedValue> TwigVoltages { get; init; }
    public required VerificationDto Verification { get; init; }
}

public sealed class CircuitSummary
{
    public int NodeCount { get; init; }
    public int BranchCount { get; init; }
    public int TwigCount { get; init; }
    public int LinkCount { get; init; }
    public required string ReferenceNode { get; init; }
    public required List<string> TreeBranchIds { get; init; }
    public required List<string> LinkBranchIds { get; init; }
}

public sealed class MatrixDto
{
    public required string[] RowLabels { get; init; }
    public required string[] ColumnLabels { get; init; }
    public required double[][] Values { get; init; }
}

public sealed class BranchResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public double Current { get; init; }
    public double Voltage { get; init; }
    public double Impedance { get; init; }
    public bool IsTwig { get; init; }
}

public sealed class NodeResult
{
    public required string Name { get; init; }
    public double Voltage { get; init; }
    public bool IsReference { get; init; }
}

public sealed class NamedValue
{
    public required string Name { get; init; }
    public double Value { get; init; }
}

public sealed class VerificationDto
{
    public double KclResidual { get; init; }
    public double KvlResidual { get; init; }
    public double CurrentResidual { get; init; }
    public bool Passed { get; init; }
}

public sealed class GraphAnalysisException(string message) : Exception(message);
