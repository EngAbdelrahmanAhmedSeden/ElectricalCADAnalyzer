using CircuitCAD.Models;

namespace CircuitCAD.Services;

internal static class LinearAlgebra
{
    public static double[][] Multiply(double[][] left, double[][] right)
    {
        int rows = left.Length, inner = right.Length, columns = right[0].Length;
        var result = Zeros(rows, columns);
        for (int i = 0; i < rows; i++)
            for (int k = 0; k < inner; k++)
            {
                double value = left[i][k];
                if (Math.Abs(value) < 1e-15) continue;
                for (int j = 0; j < columns; j++) result[i][j] += value * right[k][j];
            }
        return result;
    }

    public static double[][] Transpose(double[][] matrix, int? columns = null) =>
        Enumerable.Range(0, matrix.Length > 0 ? matrix[0].Length : columns ?? 0)
            .Select(j => matrix.Select(row => row[j]).ToArray()).ToArray();

    public static double[] MultiplyVector(double[][] matrix, double[] vector) =>
        matrix.Select(row => row.Select((value, j) => value * vector[j]).Sum()).ToArray();

    public static double[][] Inverse(double[][] matrix)
    {
        int n = matrix.Length;
        var augmented = Zeros(n, 2 * n);
        for (int i = 0; i < n; i++)
        {
            Array.Copy(matrix[i], augmented[i], n);
            augmented[i][n + i] = 1;
        }

        for (int column = 0; column < n; column++)
        {
            int pivot = Enumerable.Range(column, n - column)
                .OrderByDescending(row => Math.Abs(augmented[row][column])).First();
            if (Math.Abs(augmented[pivot][column]) < 1e-12)
                throw new GraphAnalysisException("The selected tree does not produce an independent cut-set system.");
            (augmented[column], augmented[pivot]) = (augmented[pivot], augmented[column]);
            double divisor = augmented[column][column];
            for (int j = 0; j < 2 * n; j++) augmented[column][j] /= divisor;
            for (int row = 0; row < n; row++)
            {
                if (row == column) continue;
                double factor = augmented[row][column];
                for (int j = 0; j < 2 * n; j++) augmented[row][j] -= factor * augmented[column][j];
            }
        }
        return augmented.Select(row => row.Skip(n).ToArray()).ToArray();
    }

    public static double[] Solve(double[][] matrix, double[] rhs)
    {
        int n = matrix.Length;
        var a = Zeros(n, n + 1);
        for (int i = 0; i < n; i++)
        {
            Array.Copy(matrix[i], a[i], n);
            a[i][n] = rhs[i];
        }

        for (int column = 0; column < n; column++)
        {
            int pivot = Enumerable.Range(column, n - column)
                .OrderByDescending(row => Math.Abs(a[row][column])).First();
            if (Math.Abs(a[pivot][column]) < 1e-12)
                throw new GraphAnalysisException("The circuit is singular. Check that it is connected and every branch has a finite, non-zero impedance.");
            (a[column], a[pivot]) = (a[pivot], a[column]);
            double divisor = a[column][column];
            for (int j = column; j <= n; j++) a[column][j] /= divisor;
            for (int row = 0; row < n; row++)
            {
                if (row == column) continue;
                double factor = a[row][column];
                for (int j = column; j <= n; j++) a[row][j] -= factor * a[column][j];
            }
        }
        return a.Select(row => row[n]).ToArray();
    }

    public static double[][] Zeros(int rows, int columns) =>
        Enumerable.Range(0, rows).Select(_ => new double[columns]).ToArray();

    public static double Clean(double value) => Math.Abs(value) < 1e-11 ? 0 : value;
}
