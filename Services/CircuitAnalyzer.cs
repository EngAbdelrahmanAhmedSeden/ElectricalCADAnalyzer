using CircuitCAD.Models;

namespace CircuitCAD.Services;

public static class CircuitAnalyzer
{
    public static AnalysisResponse Analyze(CircuitRequest request)
    {
        Validate(request);
        var nodes = request.Nodes.Select(n => n.Trim()).ToList();
        var branches = request.Branches;
        string reference = request.ReferenceNode.Trim();
        int n = nodes.Count, b = branches.Count, t = n - 1;

        var treeIds = ChooseTree(request, nodes);
        var treeSet = treeIds.ToHashSet();
        var ordered = branches.Where(x => treeSet.Contains(x.Id))
            .Concat(branches.Where(x => !treeSet.Contains(x.Id))).ToList();
        var links = ordered.Skip(t).ToList();

        var incidence = BuildIncidence(nodes, branches);
        var reduced = incidence.Where((_, i) => nodes[i] != reference).ToArray();
        var orderedReduced = ReorderColumns(reduced, branches, ordered);
        var treeMatrix = orderedReduced.Select(row => row.Take(t).ToArray()).ToArray();
        var cValues = LinearAlgebra.Multiply(LinearAlgebra.Inverse(treeMatrix), orderedReduced);
        SnapIntegers(cValues);

        var bValues = LinearAlgebra.Zeros(b - t, b);
        for (int loop = 0; loop < b - t; loop++)
        {
            for (int twig = 0; twig < t; twig++)
                bValues[loop][twig] = LinearAlgebra.Clean(-cValues[twig][t + loop]);
            bValues[loop][t + loop] = 1;
        }

        var y = ordered.Select(x => 1d / x.Impedance).ToArray();
        var e = ordered.Select(x => x.VoltageSource).ToArray();
        var source = ordered.Select(x => x.CurrentSource).ToArray();
        var cTranspose = LinearAlgebra.Transpose(cValues);
        var bTranspose = LinearAlgebra.Transpose(bValues, b);
        var cy = ScaleColumns(cValues, y);
        var bz = ScaleColumns(bValues, ordered.Select(x => x.Impedance).ToArray());
        var cutSystem = LinearAlgebra.Multiply(cy, cTranspose);
        var rhs = LinearAlgebra.MultiplyVector(cValues,
            source.Select((value, i) => value - y[i] * e[i]).ToArray());
        var twigVoltages = LinearAlgebra.Solve(cutSystem, rhs);
        var orderedVoltages = LinearAlgebra.MultiplyVector(cTranspose, twigVoltages);
        var orderedCurrents = orderedVoltages.Select((v, i) => y[i] * (v + e[i]) - source[i]).ToArray();

        var loopSystem = LinearAlgebra.Multiply(bz, bTranspose);
        var loopRhs = LinearAlgebra.MultiplyVector(bValues,
            e.Select((value, i) => value - ordered[i].Impedance * source[i]).ToArray());
        var loopCurrents = LinearAlgebra.Solve(loopSystem, loopRhs);
        var currentsFromLoops = LinearAlgebra.MultiplyVector(bTranspose, loopCurrents);

        var potentials = CalculateNodePotentials(nodes, ordered.Take(t).ToList(), orderedVoltages, reference);
        var resultById = ordered.Select((branch, i) => new BranchResult
        {
            Id = branch.Id,
            Name = branch.Name.Trim(),
            Current = LinearAlgebra.Clean(orderedCurrents[i]),
            Voltage = LinearAlgebra.Clean(orderedVoltages[i]),
            Impedance = branch.Impedance,
            IsTwig = i < t
        }).ToDictionary(x => x.Id);

        double kcl = MaxAbs(LinearAlgebra.MultiplyVector(ReorderColumns(incidence, branches, ordered), orderedCurrents));
        double kvl = MaxAbs(LinearAlgebra.MultiplyVector(bValues, orderedVoltages));
        double currentResidual = currentsFromLoops.Zip(orderedCurrents, (x, yValue) => Math.Abs(x - yValue)).Max();

        return new AnalysisResponse
        {
            Summary = new CircuitSummary
            {
                NodeCount = n, BranchCount = b, TwigCount = t, LinkCount = b - t,
                ReferenceNode = reference, TreeBranchIds = treeIds,
                LinkBranchIds = links.Select(x => x.Id).ToList()
            },
            Incidence = ToMatrix(nodes.ToArray(), branches.Select(x => x.Name.Trim()).ToArray(), incidence),
            ReducedIncidence = ToMatrix(nodes.Where(x => x != reference).ToArray(),
                branches.Select(x => x.Name.Trim()).ToArray(), reduced),
            TieSet = ToMatrix(links.Select(x => $"Loop {x.Name.Trim()}").ToArray(),
                ordered.Select(x => x.Name.Trim()).ToArray(), bValues),
            CutSet = ToMatrix(ordered.Take(t).Select(x => $"Cut {x.Name.Trim()}").ToArray(),
                ordered.Select(x => x.Name.Trim()).ToArray(), cValues),
            BranchResults = branches.Select(x => resultById[x.Id]).ToList(),
            NodeResults = nodes.Select(x => new NodeResult
            {
                Name = x, Voltage = LinearAlgebra.Clean(potentials[x]), IsReference = x == reference
            }).ToList(),
            LoopCurrents = links.Select((x, i) => new NamedValue
            {
                Name = $"I{L(loop: i + 1)} ({x.Name.Trim()})", Value = LinearAlgebra.Clean(loopCurrents[i])
            }).ToList(),
            TwigVoltages = ordered.Take(t).Select((x, i) => new NamedValue
            {
                Name = $"E{x.Name.Trim()}", Value = LinearAlgebra.Clean(twigVoltages[i])
            }).ToList(),
            Verification = new VerificationDto
            {
                KclResidual = kcl, KvlResidual = kvl, CurrentResidual = currentResidual,
                Passed = kcl < 1e-7 && kvl < 1e-7 && currentResidual < 1e-7
            }
        };
    }

    private static string L(int loop) => loop.ToString();

    private static void Validate(CircuitRequest request)
    {
        if (request.Nodes.Count is < 2 or > 30) throw new GraphAnalysisException("Enter between 2 and 30 nodes.");
        if (request.Branches.Count is < 1 or > 100) throw new GraphAnalysisException("Enter between 1 and 100 branches.");
        var nodes = request.Nodes.Select(x => x?.Trim() ?? string.Empty).ToList();
        if (nodes.Any(string.IsNullOrWhiteSpace) || nodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != nodes.Count)
            throw new GraphAnalysisException("Node names must be non-empty and unique.");
        if (!nodes.Contains(request.ReferenceNode?.Trim() ?? string.Empty)) throw new GraphAnalysisException("Choose a valid reference node.");
        var ids = new HashSet<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in request.Branches)
        {
            if (!ids.Add(branch.Id)) throw new GraphAnalysisException("Branch identifiers must be unique.");
            if (string.IsNullOrWhiteSpace(branch.Name) || !names.Add(branch.Name.Trim()))
                throw new GraphAnalysisException("Branch names must be non-empty and unique.");
            if (!nodes.Contains(branch.From) || !nodes.Contains(branch.To) || branch.From == branch.To)
                throw new GraphAnalysisException($"Branch {branch.Name} must connect two different, existing nodes.");
            if (!double.IsFinite(branch.Impedance) || branch.Impedance <= 0)
                throw new GraphAnalysisException($"Branch {branch.Name} needs a finite impedance greater than zero.");
            if (!double.IsFinite(branch.VoltageSource) || !double.IsFinite(branch.CurrentSource))
                throw new GraphAnalysisException($"Branch {branch.Name} has an invalid source value.");
        }
    }

    private static List<string> ChooseTree(CircuitRequest request, List<string> nodes)
    {
        if (request.TreeBranchIds is { Count: > 0 })
        {
            var ids = request.TreeBranchIds.Distinct().ToList();
            if (ids.Count != nodes.Count - 1 || ids.Count != request.TreeBranchIds.Count)
                throw new GraphAnalysisException($"Select exactly {nodes.Count - 1} distinct tree branches.");
            var selected = ids.Select(id => request.Branches.FirstOrDefault(x => x.Id == id)
                ?? throw new GraphAnalysisException("The selected tree contains an unknown branch.")).ToList();
            if (!IsTree(selected, nodes)) throw new GraphAnalysisException("Tree branches must connect every node without forming a loop.");
            return request.Branches.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToList();
        }

        var adjacency = nodes.ToDictionary(x => x, _ => new List<(string Node, string Id)>());
        foreach (var branch in request.Branches)
        {
            adjacency[branch.From].Add((branch.To, branch.Id));
            adjacency[branch.To].Add((branch.From, branch.Id));
        }
        var visited = new HashSet<string> { nodes[0] };
        var tree = new List<string>();
        var stack = new Stack<string>(); stack.Push(nodes[0]);
        while (stack.Count > 0)
        {
            string node = stack.Pop();
            foreach (var edge in adjacency[node])
            {
                if (!visited.Add(edge.Node)) continue;
                tree.Add(edge.Id); stack.Push(edge.Node);
            }
        }
        if (visited.Count != nodes.Count) throw new GraphAnalysisException("The graph must be connected before it can be analyzed.");
        return request.Branches.Where(x => tree.Contains(x.Id)).Select(x => x.Id).ToList();
    }

    private static bool IsTree(List<BranchInput> edges, List<string> nodes)
    {
        var parent = nodes.ToDictionary(x => x, x => x);
        string Root(string x) => parent[x] == x ? x : parent[x] = Root(parent[x]);
        foreach (var edge in edges)
        {
            string a = Root(edge.From), b = Root(edge.To);
            if (a == b) return false;
            parent[a] = b;
        }
        return nodes.Select(Root).Distinct().Count() == 1;
    }

    private static double[][] BuildIncidence(List<string> nodes, List<BranchInput> branches) =>
        nodes.Select(node => branches.Select(branch => branch.From == node ? 1d : branch.To == node ? -1d : 0d).ToArray()).ToArray();

    private static double[][] ReorderColumns(double[][] matrix, List<BranchInput> original, List<BranchInput> ordered)
    {
        var indices = ordered.Select(x => original.IndexOf(x)).ToArray();
        return matrix.Select(row => indices.Select(i => row[i]).ToArray()).ToArray();
    }

    private static double[][] ScaleColumns(double[][] matrix, double[] scales) =>
        matrix.Select(row => row.Select((value, i) => value * scales[i]).ToArray()).ToArray();

    private static MatrixDto ToMatrix(string[] rows, string[] columns, double[][] values) => new()
    {
        RowLabels = rows, ColumnLabels = columns,
        Values = values.Select(row => row.Select(LinearAlgebra.Clean).ToArray()).ToArray()
    };

    private static void SnapIntegers(double[][] matrix)
    {
        for (int i = 0; i < matrix.Length; i++)
            for (int j = 0; j < matrix[i].Length; j++) matrix[i][j] = Math.Round(matrix[i][j]);
    }

    private static Dictionary<string, double> CalculateNodePotentials(List<string> nodes, List<BranchInput> tree,
        double[] voltages, string reference)
    {
        var adjacency = nodes.ToDictionary(x => x, _ => new List<(string Node, double Drop)>());
        for (int i = 0; i < tree.Count; i++)
        {
            adjacency[tree[i].From].Add((tree[i].To, voltages[i]));
            adjacency[tree[i].To].Add((tree[i].From, -voltages[i]));
        }
        var result = nodes.ToDictionary(x => x, _ => 0d);
        var visited = new HashSet<string> { reference };
        var queue = new Queue<string>(); queue.Enqueue(reference);
        while (queue.Count > 0)
        {
            string node = queue.Dequeue();
            foreach (var edge in adjacency[node])
            {
                if (!visited.Add(edge.Node)) continue;
                result[edge.Node] = result[node] - edge.Drop;
                queue.Enqueue(edge.Node);
            }
        }
        return result;
    }

    private static double MaxAbs(double[] values) => values.Length == 0 ? 0 : values.Max(Math.Abs);
}
