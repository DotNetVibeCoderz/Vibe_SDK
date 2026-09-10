namespace PollenRobotics.Net.Kinematics;

/// <summary>
/// The small dense-matrix routines the IK solvers need.
/// </summary>
/// <remarks>
/// Deliberately not a linear algebra library. Every problem here is at most 7x6, small enough that
/// the whole computation fits in stack buffers and a dependency on a BLAS binding would cost more
/// than it saves - including on the ARM boards this SDK is meant to run on.
/// </remarks>
internal static class LinearAlgebra
{
    /// <summary>
    /// Solves <c>(J^T J + lambda^2 I) x = J^T r</c> - the damped least-squares step.
    /// </summary>
    /// <remarks>
    /// The damping term is what keeps the solver usable near a singularity. Plain Gauss-Newton
    /// asks for an unbounded joint velocity when the arm straightens; the damped form trades a
    /// little tracking accuracy for a step that stays finite, which on hardware is the difference
    /// between a smooth approach and the arm snapping.
    /// </remarks>
    /// <param name="jacobian">Row-major, <paramref name="rows"/> x <paramref name="columns"/>.</param>
    /// <param name="residual">Length <paramref name="rows"/>.</param>
    /// <param name="solution">Receives <paramref name="columns"/> values.</param>
    /// <param name="damping">Squared damping factor.</param>
    /// <param name="rows">Rows in the Jacobian, inferred from the spans when zero.</param>
    /// <param name="columns">Columns in the Jacobian, inferred from the spans when zero.</param>
    public static bool SolveDamped(
        ReadOnlySpan<double> jacobian,
        ReadOnlySpan<double> residual,
        Span<double> solution,
        double damping,
        int rows = 0,
        int columns = 0)
    {
        if (rows == 0)
        {
            rows = residual.Length;
        }

        if (columns == 0)
        {
            columns = solution.Length;
        }

        if (jacobian.Length < rows * columns)
        {
            throw new ArgumentException("Jacobian is smaller than the declared shape.", nameof(jacobian));
        }

        // Normal equations, built in place. columns is at most 7 here, so 49 doubles.
        Span<double> normal = stackalloc double[columns * columns];
        Span<double> rhs = stackalloc double[columns];

        for (int i = 0; i < columns; i++)
        {
            double accumulator = 0;
            for (int k = 0; k < rows; k++)
            {
                accumulator += jacobian[(k * columns) + i] * residual[k];
            }

            rhs[i] = accumulator;

            for (int j = 0; j < columns; j++)
            {
                double sum = 0;
                for (int k = 0; k < rows; k++)
                {
                    sum += jacobian[(k * columns) + i] * jacobian[(k * columns) + j];
                }

                normal[(i * columns) + j] = sum + (i == j ? damping : 0);
            }
        }

        return SolveInPlace(normal, rhs, solution, columns);
    }

    /// <summary>Gaussian elimination with partial pivoting. Destroys both inputs.</summary>
    private static bool SolveInPlace(Span<double> matrix, Span<double> rhs, Span<double> solution, int n)
    {
        for (int column = 0; column < n; column++)
        {
            int pivotRow = column;
            double best = Math.Abs(matrix[(column * n) + column]);
            for (int row = column + 1; row < n; row++)
            {
                double candidate = Math.Abs(matrix[(row * n) + column]);
                if (candidate > best)
                {
                    best = candidate;
                    pivotRow = row;
                }
            }

            if (best < 1e-14)
            {
                // Singular even after damping. The caller treats this as "no step available".
                return false;
            }

            if (pivotRow != column)
            {
                for (int k = 0; k < n; k++)
                {
                    (matrix[(column * n) + k], matrix[(pivotRow * n) + k]) = (matrix[(pivotRow * n) + k], matrix[(column * n) + k]);
                }

                (rhs[column], rhs[pivotRow]) = (rhs[pivotRow], rhs[column]);
            }

            double pivot = matrix[(column * n) + column];
            for (int row = column + 1; row < n; row++)
            {
                double factor = matrix[(row * n) + column] / pivot;
                if (factor == 0)
                {
                    continue;
                }

                for (int k = column; k < n; k++)
                {
                    matrix[(row * n) + k] -= factor * matrix[(column * n) + k];
                }

                rhs[row] -= factor * rhs[column];
            }
        }

        for (int row = n - 1; row >= 0; row--)
        {
            double sum = rhs[row];
            for (int k = row + 1; k < n; k++)
            {
                sum -= matrix[(row * n) + k] * solution[k];
            }

            solution[row] = sum / matrix[(row * n) + row];
        }

        return true;
    }
}
