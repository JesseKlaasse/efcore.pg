using Npgsql.EntityFrameworkCore.PostgreSQL.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.Mapping;
using static Npgsql.EntityFrameworkCore.PostgreSQL.Utilities.Statics;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.Query.ExpressionTranslators.Internal;

public class NpgsqlByteArrayMethodTranslator : IMethodCallTranslator
{
    private readonly ISqlExpressionFactory _sqlExpressionFactory;

    public NpgsqlByteArrayMethodTranslator(ISqlExpressionFactory sqlExpressionFactory)
        => _sqlExpressionFactory = sqlExpressionFactory;

    public virtual SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        Check.NotNull(method, nameof(method));
        Check.NotNull(arguments, nameof(arguments));

        if (method.IsGenericMethod && arguments[0].TypeMapping is NpgsqlByteArrayTypeMapping typeMapping)
        {
            // Note: we only translate if the array argument is a column mapped to bytea. There are various other
            // cases (e.g. Where(b => new byte[] { 1, 2, 3 }.Contains(b.SomeByte))) where we prefer to translate via
            // regular PostgreSQL array logic.
            if (method.GetGenericMethodDefinition().Equals(EnumerableMethods.Contains))
            {
                var source = arguments[0];

                // We have a byte value, but we need a bytea for PostgreSQL POSITION.
                var value = arguments[1] is SqlConstantExpression constantValue
                    ? (SqlExpression)_sqlExpressionFactory.Constant(new[] { (byte)constantValue.Value! }, typeMapping)
                    // Create bytea from non-constant byte: SELECT set_byte('\x00', 0, 8::smallint);
                    : _sqlExpressionFactory.Function(
                        "set_byte",
                        new[]
                        {
                            _sqlExpressionFactory.Constant(new[] { (byte)0 }, typeMapping),
                            _sqlExpressionFactory.Constant(0),
                            arguments[1]
                        },
                        nullable: true,
                        argumentsPropagateNullability: TrueArrays[3],
                        typeof(byte[]),
                        typeMapping);

                return _sqlExpressionFactory.GreaterThan(
                    PostgresFunctionExpression.CreateWithArgumentSeparators(
                        "position",
                        new[] { value, source },
                        new[] { "IN" },   // POSITION(x IN y)
                        nullable: true,
                        argumentsPropagateNullability: TrueArrays[2],
                        builtIn: true,
                        typeof(int),
                        null),
                    _sqlExpressionFactory.Constant(0));
            }

            if (method.GetGenericMethodDefinition().Equals(EnumerableMethods.FirstWithoutPredicate))
            {
                return _sqlExpressionFactory.Convert(
                    _sqlExpressionFactory.Function(
                        "get_byte",
                        new[] { arguments[0], _sqlExpressionFactory.Constant(0) },
                        nullable: true,
                        argumentsPropagateNullability: TrueArrays[2],
                        typeof(byte)),
                    method.ReturnType);
            }
        }

        return null;
    }
}