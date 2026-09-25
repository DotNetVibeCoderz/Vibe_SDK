using System.Globalization;

namespace TypeSafeAppGen.Workspace;

/// <summary>
/// Evaluator ekspresi matematika tanpa eval/kompilasi dinamis: + - * / % ^, kurung, konstanta pi/e,
/// dan fungsi sqrt, abs, sin, cos, tan, asin, acos, atan, ln, log, log2, exp, floor, ceil, round, min, max, pow.
/// Sudut trigonometri dalam radian.
/// </summary>
public sealed class ExpressionEvaluator
{
    private readonly string _text;
    private int _position;

    private ExpressionEvaluator(string text) => _text = text;

    public static double Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new FormatException("Expression is empty.");
        var parser = new ExpressionEvaluator(expression);
        var value = parser.ParseExpression();
        parser.SkipSpaces();
        if (parser._position < parser._text.Length)
            throw new FormatException($"Unexpected '{parser._text[parser._position]}' at position {parser._position + 1}.");
        return value;
    }

    private double ParseExpression()
    {
        var value = ParseTerm();
        while (true)
        {
            if (Match('+')) value += ParseTerm();
            else if (Match('-')) value -= ParseTerm();
            else return value;
        }
    }

    private double ParseTerm()
    {
        var value = ParseUnary();
        while (true)
        {
            if (Match('*')) value *= ParseUnary();
            else if (Match('/'))
            {
                var divisor = ParseUnary();
                if (divisor == 0) throw new DivideByZeroException("Division by zero.");
                value /= divisor;
            }
            else if (Match('%')) value %= ParseUnary();
            else return value;
        }
    }

    private double ParseUnary()
    {
        if (Match('-')) return -ParseUnary();
        if (Match('+')) return ParseUnary();
        return ParsePower();
    }

    // Pangkat asosiatif kanan: 2^3^2 = 2^9.
    private double ParsePower()
    {
        var value = ParsePrimary();
        return Match('^') ? Math.Pow(value, ParseUnary()) : value;
    }

    private double ParsePrimary()
    {
        SkipSpaces();
        if (Match('('))
        {
            var inner = ParseExpression();
            Expect(')');
            return inner;
        }
        if (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.')) return ParseNumber();
        if (_position < _text.Length && char.IsLetter(_text[_position])) return ParseIdentifier();
        throw new FormatException(_position < _text.Length ? $"Unexpected '{_text[_position]}' at position {_position + 1}." : "Expression ends unexpectedly.");
    }

    private double ParseNumber()
    {
        var start = _position;
        while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.')) _position++;
        if (_position < _text.Length && (_text[_position] is 'e' or 'E') && _position + 1 < _text.Length
            && (char.IsDigit(_text[_position + 1]) || _text[_position + 1] is '-' or '+'))
        {
            _position += 2;
            while (_position < _text.Length && char.IsDigit(_text[_position])) _position++;
        }
        return double.Parse(_text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private double ParseIdentifier()
    {
        var start = _position;
        while (_position < _text.Length && char.IsLetterOrDigit(_text[_position])) _position++;
        var name = _text[start.._position].ToLowerInvariant();
        switch (name)
        {
            case "pi": return Math.PI;
            case "e": return Math.E;
        }

        Expect('(');
        var args = new List<double> { ParseExpression() };
        while (Match(',')) args.Add(ParseExpression());
        Expect(')');

        double One() => args.Count == 1 ? args[0] : throw new FormatException($"{name}() takes one argument.");
        double Two(Func<double, double, double> f) => args.Count == 2 ? f(args[0], args[1]) : throw new FormatException($"{name}() takes two arguments.");

        return name switch
        {
            "sqrt" => Math.Sqrt(One()),
            "abs" => Math.Abs(One()),
            "sin" => Math.Sin(One()),
            "cos" => Math.Cos(One()),
            "tan" => Math.Tan(One()),
            "asin" => Math.Asin(One()),
            "acos" => Math.Acos(One()),
            "atan" => Math.Atan(One()),
            "ln" => Math.Log(One()),
            "log" => args.Count == 2 ? Math.Log(args[0], args[1]) : Math.Log10(One()),
            "log2" => Math.Log2(One()),
            "exp" => Math.Exp(One()),
            "floor" => Math.Floor(One()),
            "ceil" => Math.Ceiling(One()),
            "round" => args.Count == 2 ? Math.Round(args[0], (int)args[1], MidpointRounding.AwayFromZero) : Math.Round(One(), MidpointRounding.AwayFromZero),
            "min" => args.Min(),
            "max" => args.Max(),
            "pow" => Two(Math.Pow),
            _ => throw new FormatException($"Unknown function '{name}'."),
        };
    }

    private bool Match(char expected)
    {
        SkipSpaces();
        if (_position < _text.Length && _text[_position] == expected)
        {
            _position++;
            return true;
        }
        return false;
    }

    private void Expect(char expected)
    {
        if (!Match(expected)) throw new FormatException($"Expected '{expected}' at position {_position + 1}.");
    }

    private void SkipSpaces()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
    }
}
