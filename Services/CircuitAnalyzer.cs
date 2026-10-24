
using System;
using System.Collections.Generic;
using System.Linq;
using CircuitCAD.Models;

namespace CircuitCAD.Services
{
    public static class CircuitAnalyzer
    {
        private const double Tolerance = 1e-7;

        public static AnalysisResponse Analyze(CircuitRequest request)
        {
            Validate(request);

            var nodes = request.Nodes
                .Select(x => x.Trim())
                .ToList();

            var branches = request.Branches;
            string reference = request.ReferenceNode.Trim();

            int nodeCount = nodes.Count;
            int branchCount = branches.Count;
            int twigCount = nodeCount - 1;
            int linkCount = branchCount - twigCount;

            // -----------------------------------------------------
            // 1. Select a spanning tree.
            //    Internal branch order: twigs first, then links.
            // -----------------------------------------------------

            var treeIds = ChooseTree(request, nodes);
            var treeIdSet = new HashSet<string>(
                treeIds,
                StringComparer.OrdinalIgnoreCase);

            var ordered = branches
                .Where(x => treeIdSet.Contains(x.Id))
                .Concat(
                    branches.Where(
                        x => !treeIdSet.Contains(x.Id)))
                .ToList();

            var twigs = ordered.Take(twigCount).ToList();
            var links = ordered.Skip(twigCount).ToList();

            // -----------------------------------------------------
            // 2. Build A and Ared.
            //
            //    A:     nodeCount x branchCount
            //    Ared:  (nodeCount - 1) x branchCount
            // -----------------------------------------------------

            var incidence = BuildIncidence(nodes, branches);

            var reduced = incidence
                .Where((row, i) =>
                    !string.Equals(
                        nodes[i],
                        reference,
                        StringComparison.OrdinalIgnoreCase))
                .Select(row => row.ToArray())
                .ToArray();

            // Rearrange Ared columns to match twigs-first order.
            var orderedReduced = ReorderColumns(
                reduced,
                branches,
                ordered);

            // -----------------------------------------------------
            // 3. Build the fundamental cut-set matrix C.
            //
            //    Ared = [AT  AL]
            //    C = inverse(AT) * Ared = [I  CL]
            // -----------------------------------------------------

            var treeIncidence = orderedReduced
                .Select(row =>
                    row.Take(twigCount).ToArray())
                .ToArray();

            var cValues = LinearAlgebra.Multiply(
                LinearAlgebra.Inverse(treeIncidence),
                orderedReduced);

            SnapIntegers(cValues);

            // -----------------------------------------------------
            // 4. Build the fundamental tie-set matrix B.
            //
            //    With twigs first and links last:
            //    B = [-CL^T  I]
            //
            //    B has linkCount rows and branchCount columns.
            // -----------------------------------------------------

            var bValues = LinearAlgebra.Zeros(
                linkCount,
                branchCount);

            for (int loop = 0; loop < linkCount; loop++)
            {
                for (int twig = 0; twig < twigCount; twig++)
                {
                    bValues[loop][twig] =
                        LinearAlgebra.Clean(
                            -cValues[twig][twigCount + loop]);
                }

                bValues[loop][twigCount + loop] = 1.0;
            }

            var cTranspose = LinearAlgebra.Transpose(cValues);
            var bTranspose = LinearAlgebra.Transpose(
                bValues,
                branchCount);

            // -----------------------------------------------------
            // 5. Arrange electrical parameters in matrix order.
            //
            //    Zb = diagonal(z)
            //    Yb = diagonal(1/z)
            // -----------------------------------------------------

            var z = ordered
                .Select(x => x.Impedance)
                .ToArray();

            var y = z
                .Select(value => 1.0 / value)
                .ToArray();

            var vs = ordered
                .Select(x => x.VoltageSource)
                .ToArray();

            var isource = ordered
                .Select(x => x.CurrentSource)
                .ToArray();

            // =====================================================
            // 6. TIE-SET / LOOP ANALYSIS (KVL).
            //
            //    B * Zb * B^T * IL = B * Vs - B * Zb * Is
            //
            //    IL = loop currents
            //    Jb = B^T * IL
            // =====================================================

            double[] loopCurrents;
            double[] branchCurrents;

            if (linkCount == 0)
            {
                // A connected tree has no independent loops.
                // KCL requires all branch currents to be zero.
                loopCurrents = Array.Empty<double>();
                branchCurrents = new double[branchCount];
            }
            else
            {
                // B * Zb
                var bz = ScaleColumns(bValues, z);

                // B * Zb * B^T
                var loopSystem = LinearAlgebra.Multiply(
                    bz,
                    bTranspose);

                // Vs - Zb * Is
                var adjustedVs = vs
                    .Select((value, i) =>
                        value - z[i] * isource[i])
                    .ToArray();

                // B * (Vs - Zb * Is)
                var loopRhs = LinearAlgebra.MultiplyVector(
                    bValues,
                    adjustedVs);

                // Solve the linear system without explicitly
                // calculating the inverse of the loop matrix.
                loopCurrents = LinearAlgebra.Solve(
                    loopSystem,
                    loopRhs);

                // Jb = B^T * IL
                branchCurrents = LinearAlgebra.MultiplyVector(
                    bTranspose,
                    loopCurrents);
            }

            // =====================================================
            // 7. Calculate branch voltages from the branch equation.
            //
            //    Vb = Zb * (Jb + Is) - Vs
            // =====================================================

            var branchVoltages = branchCurrents
                .Select((current, i) =>
                    z[i] * (current + isource[i]) - vs[i])
                .ToArray();

            // =====================================================
            // 8. CUT-SET / NODE-PAIR ANALYSIS (KCL).
            //
            //    C * Yb * C^T * EN = C * Is - C * Yb * Vs
            //
            //    Vb = C^T * EN
            //    Jb = Yb * (Vb + Vs) - Is
            //
            //    EN here represents twig voltage coordinates
            //    associated with the selected tree.
            // =====================================================

            var cy = ScaleColumns(cValues, y);

            var cutSystem = LinearAlgebra.Multiply(
                cy,
                cTranspose);

            var adjustedIs = isource
                .Select((value, i) =>
                    value - y[i] * vs[i])
                .ToArray();

            var cutRhs = LinearAlgebra.MultiplyVector(
                cValues,
                adjustedIs);

            var twigVoltages = LinearAlgebra.Solve(
                cutSystem,
                cutRhs);

            // Branch voltages from cut-set analysis.
            var cutVoltages = LinearAlgebra.MultiplyVector(
                cTranspose,
                twigVoltages);

            // Branch currents from cut-set analysis.
            var cutCurrents = cutVoltages
                .Select((voltage, i) =>
                    y[i] * (voltage + vs[i]) - isource[i])
                .ToArray();

            // =====================================================
            // 9. Calculate node potentials relative to reference.
            //
            //    Vbranch = VFrom - VTo
            // =====================================================

            var nodePotentials = CalculateNodePotentials(
                nodes,
                twigs,
                branchVoltages.Take(twigCount).ToArray(),
                reference);

            // -----------------------------------------------------
            // 10. Map calculated values back to original branch IDs.
            // -----------------------------------------------------

            var resultById = ordered
                .Select((branch, i) => new BranchResult
                {
                    Id = branch.Id,
                    Name = branch.Name.Trim(),
                    Current = LinearAlgebra.Clean(
                        branchCurrents[i]),
                    Voltage = LinearAlgebra.Clean(
                        branchVoltages[i]),
                    Impedance = branch.Impedance,
                    IsTwig = i < twigCount
                })
                .ToDictionary(
                    x => x.Id,
                    StringComparer.OrdinalIgnoreCase);

            // -----------------------------------------------------
            // 11. Verify KCL, KVL and independent solutions.
            // -----------------------------------------------------

            // KCL: A * Jb = 0
            var orderedIncidence = ReorderColumns(
                incidence,
                branches,
                ordered);

            double kclResidual = MaxAbs(
                LinearAlgebra.MultiplyVector(
                    orderedIncidence,
                    branchCurrents));

            // KVL: B * Vb = 0
            double kvlResidual = MaxAbs(
                LinearAlgebra.MultiplyVector(
                    bValues,
                    branchVoltages));

            // Compare currents from cut-set and tie-set methods.
            double currentResidual = MaxAbs(
                cutCurrents.Zip(
                    branchCurrents,
                    (cut, loop) => cut - loop)
                .ToArray());

            // Compare branch voltages from both methods.
            double voltageResidual = MaxAbs(
                cutVoltages.Zip(
                    branchVoltages,
                    (cut, loop) => cut - loop)
                .ToArray());

            bool passed =
                kclResidual < Tolerance &&
                kvlResidual < Tolerance &&
                currentResidual < Tolerance &&
                voltageResidual < Tolerance;

            // -----------------------------------------------------
            // 12. Return results using the existing project models.
            // -----------------------------------------------------

            return new AnalysisResponse
            {
                Summary = new CircuitSummary
                {
                    NodeCount = nodeCount,
                    BranchCount = branchCount,
                    TwigCount = twigCount,
                    LinkCount = linkCount,
                    ReferenceNode = reference,
                    TreeBranchIds = treeIds,
                    LinkBranchIds = links
                        .Select(x => x.Id)
                        .ToList()
                },

                Incidence = ToMatrix(
                    nodes.ToArray(),
                    branches.Select(
                        x => x.Name.Trim()).ToArray(),
                    incidence),

                ReducedIncidence = ToMatrix(
                    nodes.Where(x =>
                        !string.Equals(
                            x,
                            reference,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray(),
                    branches.Select(
                        x => x.Name.Trim()).ToArray(),
                    reduced),

                // Matrix columns are displayed in the original
                // branch order, while internal calculations remain
                // in twigs-first order.
                TieSet = ToMatrix(
                    links.Select(
                        x => $"Loop {x.Name.Trim()}").ToArray(),
                    branches.Select(
                        x => x.Name.Trim()).ToArray(),
                    ReorderMatrixColumns(
                        bValues,
                        ordered,
                        branches)),

                CutSet = ToMatrix(
                    twigs.Select(
                        x => $"Cut {x.Name.Trim()}").ToArray(),
                    branches.Select(
                        x => x.Name.Trim()).ToArray(),
                    ReorderMatrixColumns(
                        cValues,
                        ordered,
                        branches)),

                BranchResults = branches
                    .Select(x => resultById[x.Id])
                    .ToList(),

                NodeResults = nodes
                    .Select(x => new NodeResult
                    {
                        Name = x,
                        Voltage = LinearAlgebra.Clean(
                            nodePotentials[x]),
                        IsReference = string.Equals(
                            x,
                            reference,
                            StringComparison.OrdinalIgnoreCase)
                    })
                    .ToList(),

                LoopCurrents = links
                    .Select((x, i) => new NamedValue
                    {
                        Name = $"I{i + 1} ({x.Name.Trim()})",
                        Value = LinearAlgebra.Clean(
                            loopCurrents[i])
                    })
                    .ToList(),

                TwigVoltages = twigs
                    .Select((x, i) => new NamedValue
                    {
                        Name = $"E{x.Name.Trim()}",
                        Value = LinearAlgebra.Clean(
                            twigVoltages[i])
                    })
                    .ToList(),

                Verification = new VerificationDto
                {
                    KclResidual = kclResidual,
                    KvlResidual = kvlResidual,
                    CurrentResidual = currentResidual,
                    Passed = passed
                }
            };
        }

        // =========================================================
        // INPUT VALIDATION
        // =========================================================

        private static void Validate(CircuitRequest request)
        {
            if (request == null)
            {
                throw new GraphAnalysisException(
                    "A circuit request is required.");
            }

            if (request.Nodes == null ||
                request.Nodes.Count < 2 ||
                request.Nodes.Count > 30)
            {
                throw new GraphAnalysisException(
                    "Enter between 2 and 30 nodes.");
            }

            if (request.Branches == null ||
                request.Branches.Count < 1 ||
                request.Branches.Count > 100)
            {
                throw new GraphAnalysisException(
                    "Enter between 1 and 100 branches.");
            }

            var nodes = request.Nodes
                .Select(x => x?.Trim() ?? string.Empty)
                .ToList();

            if (nodes.Any(string.IsNullOrWhiteSpace) ||
                nodes.Distinct(
                    StringComparer.OrdinalIgnoreCase).Count()
                    != nodes.Count)
            {
                throw new GraphAnalysisException(
                    "Node names must be non-empty and unique.");
            }

            string reference =
                request.ReferenceNode?.Trim() ?? string.Empty;

            if (!nodes.Contains(
                reference,
                StringComparer.OrdinalIgnoreCase))
            {
                throw new GraphAnalysisException(
                    "Choose a valid reference node.");
            }

            var ids = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            var names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var branch in request.Branches)
            {
                if (branch == null)
                {
                    throw new GraphAnalysisException(
                        "A branch cannot be null.");
                }

                if (string.IsNullOrWhiteSpace(branch.Id) ||
                    !ids.Add(branch.Id.Trim()))
                {
                    throw new GraphAnalysisException(
                        "Branch IDs must be non-empty and unique.");
                }

                if (string.IsNullOrWhiteSpace(branch.Name) ||
                    !names.Add(branch.Name.Trim()))
                {
                    throw new GraphAnalysisException(
                        "Branch names must be non-empty and unique.");
                }

                string from = branch.From?.Trim() ?? string.Empty;
                string to = branch.To?.Trim() ?? string.Empty;

                if (!nodes.Contains(
                        from,
                        StringComparer.OrdinalIgnoreCase) ||
                    !nodes.Contains(
                        to,
                        StringComparer.OrdinalIgnoreCase) ||
                    string.Equals(
                        from,
                        to,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new GraphAnalysisException(
                        $"Branch {branch.Name} must connect two " +
                        "different existing nodes.");
                }

                if (!double.IsFinite(branch.Impedance) ||
                    branch.Impedance <= 0)
                {
                    throw new GraphAnalysisException(
                        $"Branch {branch.Name} needs a finite, " +
                        "positive impedance.");
                }

                if (!double.IsFinite(branch.VoltageSource) ||
                    !double.IsFinite(branch.CurrentSource))
                {
                    throw new GraphAnalysisException(
                        $"Branch {branch.Name} has an invalid source.");
                }
            }

            // A connected graph needs at least n - 1 branches.
            if (request.Branches.Count < nodes.Count - 1)
            {
                throw new GraphAnalysisException(
                    "The graph cannot connect all nodes: " +
                    "at least n - 1 branches are required.");
            }
        }

        // =========================================================
        // SPANNING TREE
        // =========================================================

        private static List<string> ChooseTree(
            CircuitRequest request,
            List<string> nodes)
        {
            if (request.TreeBranchIds != null &&
                request.TreeBranchIds.Count > 0)
            {
                var requestedIds = request.TreeBranchIds
                    .Select(x => x?.Trim() ?? string.Empty)
                    .ToList();

                var uniqueIds = new HashSet<string>(
                    requestedIds,
                    StringComparer.OrdinalIgnoreCase);

                if (requestedIds.Any(string.IsNullOrWhiteSpace) ||
                    uniqueIds.Count != nodes.Count - 1 ||
                    uniqueIds.Count != requestedIds.Count)
                {
                    throw new GraphAnalysisException(
                        $"Select exactly {nodes.Count - 1} " +
                        "distinct tree branches.");
                }

                var selected = requestedIds
                    .Select(id =>
                        request.Branches.FirstOrDefault(
                            x => string.Equals(
                                x.Id,
                                id,
                                StringComparison.OrdinalIgnoreCase))
                        ?? throw new GraphAnalysisException(
                            $"Unknown tree branch ID: {id}."))
                    .ToList();

                if (!IsTree(selected, nodes))
                {
                    throw new GraphAnalysisException(
                        "Selected tree branches must connect all " +
                        "nodes without forming a loop.");
                }

                // Preserve the requested branch-list ordering
                // consistently with the rest of the analysis.
                return request.Branches
                    .Where(x => uniqueIds.Contains(x.Id))
                    .Select(x => x.Id)
                    .ToList();
            }

            var adjacency = nodes.ToDictionary(
                x => x,
                _ => new List<(string Node, string BranchId)>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var branch in request.Branches)
            {
                string from = branch.From.Trim();
                string to = branch.To.Trim();

                adjacency[from].Add((to, branch.Id));
                adjacency[to].Add((from, branch.Id));
            }

            var visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            var tree = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            var queue = new Queue<string>();

            visited.Add(nodes[0]);
            queue.Enqueue(nodes[0]);

            while (queue.Count > 0)
            {
                string node = queue.Dequeue();

                foreach (var edge in adjacency[node])
                {
                    if (!visited.Add(edge.Node))
                        continue;

                    tree.Add(edge.BranchId);
                    queue.Enqueue(edge.Node);
                }
            }

            if (visited.Count != nodes.Count)
            {
                throw new GraphAnalysisException(
                    "The graph must be connected before analysis.");
            }

            return request.Branches
                .Where(x => tree.Contains(x.Id))
                .Select(x => x.Id)
                .ToList();
        }

        private static bool IsTree(
            List<BranchInput> edges,
            List<string> nodes)
        {
            var parent = nodes.ToDictionary(
                x => x,
                x => x,
                StringComparer.OrdinalIgnoreCase);

            string Root(string node)
            {
                if (string.Equals(
                    parent[node],
                    node,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                parent[node] = Root(parent[node]);
                return parent[node];
            }

            foreach (var edge in edges)
            {
                string a = Root(edge.From.Trim());
                string b = Root(edge.To.Trim());

                if (string.Equals(
                    a,
                    b,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                parent[a] = b;
            }

            return nodes
                .Select(Root)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1;
        }

        // =========================================================
        // MATRIX HELPERS
        // =========================================================

        private static double[][] BuildIncidence(
            List<string> nodes,
            List<BranchInput> branches)
        {
            return nodes
                .Select(node =>
                    branches.Select(branch =>
                    {
                        if (string.Equals(
                            branch.From.Trim(),
                            node,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return 1.0;
                        }

                        if (string.Equals(
                            branch.To.Trim(),
                            node,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return -1.0;
                        }

                        return 0.0;
                    }).ToArray())
                .ToArray();
        }

        private static double[][] ReorderColumns(
            double[][] matrix,
            List<BranchInput> original,
            List<BranchInput> desired)
        {
            var indexById = original
                .Select((branch, index) =>
                    new { branch.Id, Index = index })
                .ToDictionary(
                    x => x.Id,
                    x => x.Index,
                    StringComparer.OrdinalIgnoreCase);

            return matrix
                .Select(row =>
                    desired.Select(
                        branch => row[indexById[branch.Id]])
                    .ToArray())
                .ToArray();
        }

        private static double[][] ReorderMatrixColumns(
            double[][] matrix,
            List<BranchInput> currentOrder,
            List<BranchInput> desiredOrder)
        {
            var indexById = currentOrder
                .Select((branch, index) =>
                    new { branch.Id, Index = index })
                .ToDictionary(
                    x => x.Id,
                    x => x.Index,
                    StringComparer.OrdinalIgnoreCase);

            return matrix
                .Select(row =>
                    desiredOrder.Select(
                        branch => row[indexById[branch.Id]])
                    .ToArray())
                .ToArray();
        }

        private static double[][] ScaleColumns(
            double[][] matrix,
            double[] scales)
        {
            return matrix
                .Select(row =>
                    row.Select((value, i) =>
                        value * scales[i])
                    .ToArray())
                .ToArray();
        }

        private static MatrixDto ToMatrix(
            string[] rows,
            string[] columns,
            double[][] values)
        {
            return new MatrixDto
            {
                RowLabels = rows,
                ColumnLabels = columns,
                Values = values
                    .Select(row =>
                        row.Select(LinearAlgebra.Clean).ToArray())
                    .ToArray()
            };
        }

        private static void SnapIntegers(double[][] matrix)
        {
            for (int i = 0; i < matrix.Length; i++)
            {
                for (int j = 0; j < matrix[i].Length; j++)
                {
                    double value = matrix[i][j];
                    double rounded = Math.Round(value);

                    // Only snap values that are already very close
                    // to integers. Never hide a calculation error.
                    if (Math.Abs(value - rounded) < 1e-9)
                    {
                        matrix[i][j] = rounded;
                    }
                }
            }
        }

        // =========================================================
        // NODE POTENTIALS
        // =========================================================

        private static Dictionary<string, double>
            CalculateNodePotentials(
                List<string> nodes,
                List<BranchInput> tree,
                double[] twigVoltages,
                string reference)
        {
            // With the incidence convention:
            // Vbranch = VFrom - VTo
            var adjacency = nodes.ToDictionary(
                x => x,
                _ => new List<(string Node, double Drop)>(),
                StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < tree.Count; i++)
            {
                string from = tree[i].From.Trim();
                string to = tree[i].To.Trim();
                double voltage = twigVoltages[i];

                adjacency[from].Add((to, voltage));
                adjacency[to].Add((from, -voltage));
            }

            var potentials = nodes.ToDictionary(
                x => x,
                _ => 0.0,
                StringComparer.OrdinalIgnoreCase);

            var visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                reference
            };

            var queue = new Queue<string>();
            queue.Enqueue(reference);

            while (queue.Count > 0)
            {
                string node = queue.Dequeue();

                foreach (var edge in adjacency[node])
                {
                    if (!visited.Add(edge.Node))
                        continue;

                    potentials[edge.Node] =
                        potentials[node] - edge.Drop;

                    queue.Enqueue(edge.Node);
                }
            }

            if (visited.Count != nodes.Count)
            {
                throw new GraphAnalysisException(
                    "Unable to calculate all node potentials " +
                    "from the selected spanning tree.");
            }

            // Enforce the datum-node voltage explicitly.
            potentials[reference] = 0.0;

            return potentials;
        }

        private static double MaxAbs(double[] values)
        {
            return values.Length == 0
                ? 0.0
                : values.Max(Math.Abs);
        }
    }
}
