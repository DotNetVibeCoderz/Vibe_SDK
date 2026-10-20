namespace SiWare.Analysis;

/// <summary>Small dense solvers for the few-by-few systems in Savitzky-Golay and PLS; inputs are not modified.</summary>
internal static class LinearAlgebra
{
    /// <summary>Solves <c>A x = b</c> by Gaussian elimination with partial pivoting.</summary>
    public static double[] Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        if (a.GetLength(0) != n || a.GetLength(1) != n) throw new ArgumentException("Matrix and vector sizes differ.");
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (Math.Abs(m[pivot, col]) < 1e-14) throw new ArithmeticException("Matrix is singular.");
            if (pivot != col)
            {
                for (var k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                (x[col], x[pivot]) = (x[pivot], x[col]);
            }
            for (var row = col + 1; row < n; row++)
            {
                var factor = m[row, col] / m[col, col];
                if (factor == 0) continue;
                for (var k = col; k < n; k++) m[row, k] -= factor * m[col, k];
                x[row] -= factor * x[col];
            }
        }
        for (var row = n - 1; row >= 0; row--)
        {
            var sum = x[row];
            for (var k = row + 1; k < n; k++) sum -= m[row, k] * x[k];
            x[row] = sum / m[row, row];
        }
        return x;
    }
}
