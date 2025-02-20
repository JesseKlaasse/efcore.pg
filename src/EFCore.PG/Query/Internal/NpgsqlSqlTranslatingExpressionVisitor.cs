﻿using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Npgsql.EntityFrameworkCore.PostgreSQL.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.ExpressionTranslators.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.Mapping;
using static Npgsql.EntityFrameworkCore.PostgreSQL.Utilities.Statics;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

public class NpgsqlSqlTranslatingExpressionVisitor : RelationalSqlTranslatingExpressionVisitor
{
    private static readonly ConstructorInfo DateTimeCtor1 =
        typeof(DateTime).GetConstructor(new[] { typeof(int), typeof(int), typeof(int) })!;

    private static readonly ConstructorInfo DateTimeCtor2 =
        typeof(DateTime).GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) })!;

    private static readonly ConstructorInfo DateTimeCtor3 =
        typeof(DateTime).GetConstructor(
            new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(DateTimeKind) })!;

    private static readonly ConstructorInfo DateOnlyCtor =
        typeof(DateOnly).GetConstructor(new[] { typeof(int), typeof(int), typeof(int) })!;

    private static readonly MethodInfo Like2MethodInfo =
        typeof(DbFunctionsExtensions).GetRuntimeMethod(
            nameof(DbFunctionsExtensions.Like), new[] { typeof(DbFunctions), typeof(string), typeof(string) })!;

    // ReSharper disable once InconsistentNaming
    private static readonly MethodInfo ILike2MethodInfo
        = typeof(NpgsqlDbFunctionsExtensions).GetRuntimeMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike), new[] { typeof(DbFunctions), typeof(string), typeof(string) })!;

    private static readonly MethodInfo ObjectEquals
        = typeof(object).GetRuntimeMethod(nameof(object.Equals), new[] { typeof(object), typeof(object) })!;

    private readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly NpgsqlJsonPocoTranslator _jsonPocoTranslator;
    private readonly NpgsqlLTreeTranslator _ltreeTranslator;

    private readonly RelationalTypeMapping _timestampMapping;
    private readonly RelationalTypeMapping _timestampTzMapping;

    private static Type? _nodaTimePeriodType;

    public NpgsqlSqlTranslatingExpressionVisitor(
        RelationalSqlTranslatingExpressionVisitorDependencies dependencies,
        QueryCompilationContext queryCompilationContext,
        QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor)
        : base(dependencies, queryCompilationContext, queryableMethodTranslatingExpressionVisitor)
    {
        _sqlExpressionFactory = (NpgsqlSqlExpressionFactory)dependencies.SqlExpressionFactory;
        _jsonPocoTranslator = ((NpgsqlMemberTranslatorProvider)Dependencies.MemberTranslatorProvider).JsonPocoTranslator;
        _ltreeTranslator = ((NpgsqlMethodCallTranslatorProvider)Dependencies.MethodCallTranslatorProvider).LTreeTranslator;
        _typeMappingSource = dependencies.TypeMappingSource;
        _timestampMapping = _typeMappingSource.FindMapping("timestamp without time zone")!;
        _timestampTzMapping = _typeMappingSource.FindMapping("timestamp with time zone")!;
    }

    /// <inheritdoc />
    protected override Expression VisitUnary(UnaryExpression unaryExpression)
    {
        switch (unaryExpression.NodeType)
        {
            case ExpressionType.ArrayLength:
                if (TranslationFailed(unaryExpression.Operand, Visit(unaryExpression.Operand), out var sqlOperand))
                {
                    return QueryCompilationContext.NotTranslatedExpression;
                }

                // Translate Length on byte[], but only if the type mapping is for bytea. There's also array of bytes
                // (mapped to smallint[]), which is handled below with CARDINALITY.
                if (sqlOperand!.Type == typeof(byte[])
                    && (sqlOperand.TypeMapping is null || sqlOperand.TypeMapping is NpgsqlByteArrayTypeMapping))
                {
                    return _sqlExpressionFactory.Function(
                        "length",
                        new[] { sqlOperand },
                        nullable: true,
                        argumentsPropagateNullability: TrueArrays[1],
                        typeof(int));
                }

                return _jsonPocoTranslator.TranslateArrayLength(sqlOperand)
                    ?? _sqlExpressionFactory.Function(
                        "cardinality",
                        new[] { sqlOperand },
                        nullable: true,
                        argumentsPropagateNullability: TrueArrays[1],
                        typeof(int));

            // We have row value comparison methods such as EF.Functions.GreaterThan, which accept two ValueTuples/Tuples.
            // Since they accept ITuple parameters, the arguments have a Convert node casting up from the concrete argument to ITuple;
            // this node causes translation failure in RelationalSqlTranslatingExpressionVisitor, so unwrap here.
            case ExpressionType.Convert
                when unaryExpression.Type == typeof(ITuple) && unaryExpression.Operand.Type.IsAssignableTo(typeof(ITuple)):
                return Visit(unaryExpression.Operand);
        }

        return base.VisitUnary(unaryExpression);
    }

    protected override Expression VisitMethodCall(MethodCallExpression methodCall)
    {
        if (methodCall.Arguments.Count > 0 &&
            methodCall.Arguments[0].Type.IsArrayOrGenericList() &&
            VisitArrayMethodCall(methodCall.Method, methodCall.Arguments) is { } visited)
        {
            return visited;
        }

        return base.VisitMethodCall(methodCall);
    }

    /// <summary>
    /// Identifies complex array-related constructs which cannot be translated in regular method translators, since
    /// they require accessing lambdas.
    /// </summary>
    private Expression? VisitArrayMethodCall(MethodInfo method, ReadOnlyCollection<Expression> arguments)
    {
        var array = arguments[0];
        {
            if (method.IsClosedFormOf(EnumerableMethods.AnyWithPredicate) &&
                arguments[1] is LambdaExpression wherePredicate)
            {
                if (wherePredicate.Body is MethodCallExpression wherePredicateMethodCall)
                {
                    var predicateMethod = wherePredicateMethodCall.Method;
                    var predicateArguments = wherePredicateMethodCall.Arguments;

                    // Pattern match: new[] { "a", "b", "c" }.Any(p => EF.Functions.Like(e.SomeText, p))
                    // Translation: s.SomeText LIKE ANY (ARRAY['a','b','c'])
                    // Note: we also handle the this equality instead of Like, see NpgsqlArrayMethodTranslator
                    if ((predicateMethod == Like2MethodInfo || predicateMethod == ILike2MethodInfo) &&
                        predicateArguments[2] == wherePredicate.Parameters[0])
                    {
                        return _sqlExpressionFactory.Any(
                            (SqlExpression)Visit(predicateArguments[1]),
                            (SqlExpression)Visit(array),
                            wherePredicateMethodCall.Method == Like2MethodInfo
                                ? PostgresAnyOperatorType.Like
                                : PostgresAnyOperatorType.ILike);
                    }

                    // Pattern match: new[] { 4, 5 }.Any(p => e.SomeArray.Contains(p))
                    // Translation: s.SomeArray && ARRAY[4, 5] (array overlap).
                    if (predicateMethod.IsClosedFormOf(EnumerableMethods.Contains) &&
                        predicateArguments[0].Type.IsArrayOrGenericList() &&
                        predicateArguments[1] is ParameterExpression parameterExpression1 &&
                        parameterExpression1 == wherePredicate.Parameters[0])
                    {
                        return _sqlExpressionFactory.Overlaps(
                            (SqlExpression)Visit(arguments[0]),
                            (SqlExpression)Visit(wherePredicateMethodCall.Arguments[0]));
                    }

                    // As above, but for Contains on List<T>
                    if (predicateMethod.DeclaringType?.IsGenericType == true &&
                        predicateMethod.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                        predicateMethod.Name == nameof(List<int>.Contains) &&
                        predicateMethod.GetParameters().Length == 1 &&
                        predicateArguments[0] is ParameterExpression parameterExpression2 &&
                        parameterExpression2 == wherePredicate.Parameters[0])
                    {
                        return _sqlExpressionFactory.Overlaps(
                            (SqlExpression)Visit(arguments[0]),
                            (SqlExpression)Visit(wherePredicateMethodCall.Object!));
                    }
                    
                    // As above, but for Contains on HashSet<T>
                    if (predicateMethod.DeclaringType?.IsGenericType == true &&
                        predicateMethod.DeclaringType.GetGenericTypeDefinition() == typeof(HashSet<>) &&
                        predicateMethod.Name == nameof(HashSet<int>.Contains) &&
                        predicateMethod.GetParameters().Length == 1 &&
                        predicateArguments[0] is ParameterExpression parameterExpression3 &&
                        parameterExpression3 == wherePredicate.Parameters[0])
                    {
                        return _sqlExpressionFactory.Overlaps(
                            (SqlExpression)Visit(arguments[0]),
                            (SqlExpression)Visit(wherePredicateMethodCall.Object!));
                    }

                }

                // Pattern match for: array.Any(e => e == x) (and other equality patterns)
                // Transform this to Contains.
                if (TryMatchEquality(wherePredicate.Body, out var left, out var right) &&
                    (left == wherePredicate.Parameters[0] || right == wherePredicate.Parameters[0]))
                {
                    var item = left == wherePredicate.Parameters[0]
                        ? right
                        : right == wherePredicate.Parameters[0]
                            ? left
                            : null;

                    return item is null
                        ? null
                        : Visit(Expression.Call(EnumerableMethods.Contains.MakeGenericMethod(method.GetGenericArguments()[0]), array,
                            item));
                }

                static bool TryMatchEquality(
                    Expression expression,
                    [NotNullWhen(true)] out Expression? left,
                    [NotNullWhen(true)] out Expression? right)
                {
                    if (expression is BinaryExpression binary)
                    {
                        (left, right) = (binary.Left, binary.Right);
                        return true;
                    }

                    if (expression is MethodCallExpression methodCall)
                    {
                        if (methodCall.Method == ObjectEquals)
                        {
                            (left, right) = (methodCall.Arguments[0], methodCall.Arguments[1]);
                            return true;
                        }

                        if (methodCall.Method.Name == nameof(object.Equals) && methodCall.Arguments.Count == 1)
                        {
                            (left, right) = (methodCall.Object!, methodCall.Arguments[0]);
                            return true;
                        }
                    }

                    (left, right) = (null, null);
                    return false;
                }
            }
        }

        {
            if (method.IsClosedFormOf(EnumerableMethods.All) &&
                arguments[1] is LambdaExpression wherePredicate &&
                wherePredicate.Body is MethodCallExpression wherePredicateMethodCall)
            {
                var predicateMethod = wherePredicateMethodCall.Method;
                var predicateArguments = wherePredicateMethodCall.Arguments;

                // Pattern match for: new[] { "a", "b", "c" }.All(p => EF.Functions.Like(e.SomeText, p)),
                // which we translate to WHERE s.""SomeText"" LIKE ALL (ARRAY['a','b','c'])
                if ((predicateMethod == Like2MethodInfo || predicateMethod == ILike2MethodInfo) &&
                    predicateArguments[2] == wherePredicate.Parameters[0])
                {
                    return _sqlExpressionFactory.All(
                        (SqlExpression)Visit(predicateArguments[1]),
                        (SqlExpression)Visit(arguments[0]),
                        wherePredicateMethodCall.Method == Like2MethodInfo
                            ? PostgresAllOperatorType.Like : PostgresAllOperatorType.ILike);
                }

                // Pattern match for: new[] { 4, 5 }.All(p => e.SomeArray.Contains(p)),
                // using array containment (<@)
                if (predicateMethod.IsClosedFormOf(EnumerableMethods.Contains) &&
                    predicateArguments[0].Type.IsArrayOrGenericList() &&
                    predicateArguments[1] is ParameterExpression parameterExpression &&
                    parameterExpression == wherePredicate.Parameters[0])
                {
                    return _sqlExpressionFactory.ContainedBy(
                        (SqlExpression)Visit(arguments[0]),
                        (SqlExpression)Visit(predicateArguments[0]));
                }

                // As above, but for Contains on List<T>
                if (predicateMethod.DeclaringType?.IsGenericType == true &&
                    predicateMethod.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                    predicateMethod.Name == nameof(List<int>.Contains) &&
                    predicateMethod.GetParameters().Length == 1 &&
                    predicateArguments[0] is ParameterExpression parameterExpression2 &&
                    parameterExpression2 == wherePredicate.Parameters[0])
                {
                    return _sqlExpressionFactory.ContainedBy(
                        (SqlExpression)Visit(arguments[0]),
                        (SqlExpression)Visit(wherePredicateMethodCall.Object!));
                }
                
                // As above, but for Contains on HashSet<T>
                if (predicateMethod.DeclaringType?.IsGenericType == true &&
                    predicateMethod.DeclaringType.GetGenericTypeDefinition() == typeof(HashSet<>) &&
                    predicateMethod.Name == nameof(HashSet<int>.Contains) &&
                    predicateMethod.GetParameters().Length == 1 &&
                    predicateArguments[0] is ParameterExpression parameterExpression3 &&
                    parameterExpression3 == wherePredicate.Parameters[0])
                {
                    return _sqlExpressionFactory.ContainedBy(
                        (SqlExpression)Visit(arguments[0]),
                        (SqlExpression)Visit(wherePredicateMethodCall.Object!));
                }

            }
        }

        return _ltreeTranslator.VisitArrayMethodCall(this, method, arguments);
    }

    /// <inheritdoc />
    protected override Expression VisitNewArray(NewArrayExpression newArrayExpression)
    {
        if (base.VisitNewArray(newArrayExpression) is SqlExpression visitedNewArrayExpression)
        {
            return visitedNewArrayExpression;
        }

        if (newArrayExpression.NodeType == ExpressionType.NewArrayInit)
        {
            var visitedExpressions = new SqlExpression[newArrayExpression.Expressions.Count];
            for (var i = 0; i < newArrayExpression.Expressions.Count; i++)
            {
                if (Visit(newArrayExpression.Expressions[i]) is SqlExpression visited)
                {
                    visitedExpressions[i] = visited;
                }
                else
                {
                    return QueryCompilationContext.NotTranslatedExpression;
                }
            }

            return _sqlExpressionFactory.NewArray(visitedExpressions, newArrayExpression.Type);
        }

        return QueryCompilationContext.NotTranslatedExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitBinary(BinaryExpression binaryExpression)
    {
        if (binaryExpression.NodeType == ExpressionType.Subtract)
        {
            if (binaryExpression.Left.Type.UnwrapNullableType().FullName == "NodaTime.LocalDate" &&
                binaryExpression.Right.Type.UnwrapNullableType().FullName == "NodaTime.LocalDate")
            {
                if (TranslationFailed(binaryExpression.Left, Visit(TryRemoveImplicitConvert(binaryExpression.Left)), out var sqlLeft)
                    || TranslationFailed(binaryExpression.Right, Visit(TryRemoveImplicitConvert(binaryExpression.Right)), out var sqlRight))
                {
                    return QueryCompilationContext.NotTranslatedExpression;
                }

                var subtraction = _sqlExpressionFactory.MakeBinary(
                    ExpressionType.Subtract, sqlLeft!, sqlRight!, _typeMappingSource.FindMapping(typeof(int)))!;

                return PostgresFunctionExpression.CreateWithNamedArguments(
                    "make_interval",
                    new[] {  subtraction },
                    new[] { "days" },
                    nullable: true,
                    argumentsPropagateNullability: TrueArrays[1],
                    builtIn: true,
                    _nodaTimePeriodType ??= binaryExpression.Left.Type.Assembly.GetType("NodaTime.Period")!,
                    typeMapping: null);
            }

            // Note: many other date/time arithmetic operators are fully supported as-is by PostgreSQL - see NpgsqlSqlExpressionFactory
        }

        if (binaryExpression.NodeType == ExpressionType.ArrayIndex)
        {
            if (TranslationFailed(binaryExpression.Left, Visit(TryRemoveImplicitConvert(binaryExpression.Left)), out var sqlLeft)
                || TranslationFailed(binaryExpression.Right, Visit(TryRemoveImplicitConvert(binaryExpression.Right)), out var sqlRight))
            {
                return QueryCompilationContext.NotTranslatedExpression;
            }

            // ArrayIndex over bytea is special, we have to use function rather than subscript
            if (binaryExpression.Left.Type == typeof(byte[]))
            {
                return _sqlExpressionFactory.Function(
                    "get_byte",
                    new[] { sqlLeft!, sqlRight! },
                    nullable: true,
                    argumentsPropagateNullability: TrueArrays[2],
                    typeof(byte));
            }

            return
                // Try translating ArrayIndex inside json column
                _jsonPocoTranslator.TranslateMemberAccess(sqlLeft!, sqlRight!, binaryExpression.Type) ??
                // Other types should be subscriptable - but PostgreSQL arrays are 1-based, so adjust the index.
                _sqlExpressionFactory.ArrayIndex(sqlLeft!, _sqlExpressionFactory.GenerateOneBasedIndexExpression(sqlRight!));
        }

        return base.VisitBinary(binaryExpression);
    }

    protected override Expression VisitNew(NewExpression newExpression)
    {
        var visitedNewExpression = base.VisitNew(newExpression);

        if (visitedNewExpression != QueryCompilationContext.NotTranslatedExpression)
        {
            return visitedNewExpression;
        }

        // We translate new ValueTuple<T1, T2...>(x, y...) to a SQL row value expression: (x, y).
        // This is notably done to support row value comparisons: WHERE (x, y) > (3, 4) (see e.g. NpgsqlDbFunctionsExtensions.GreaterThan)
        if (newExpression.Type.IsAssignableTo(typeof(ITuple)))
        {
            return TryTranslateArguments(out var sqlArguments)
                ? new PostgresRowValueExpression(sqlArguments, newExpression.Type)
                : QueryCompilationContext.NotTranslatedExpression;
        }

        // Translate new DateTime(...) -> make_timestamp/make_date
        if (newExpression.Constructor?.DeclaringType == typeof(DateTime))
        {
            if (newExpression.Constructor == DateTimeCtor1)
            {
                return TryTranslateArguments(out var sqlArguments)
                    ? _sqlExpressionFactory.Function(
                        "make_date", sqlArguments, nullable: true, TrueArrays[3], typeof(DateTime), _timestampMapping)
                    : QueryCompilationContext.NotTranslatedExpression;
            }

            if (newExpression.Constructor == DateTimeCtor2)
            {
                if (!TryTranslateArguments(out var sqlArguments))
                {
                    return QueryCompilationContext.NotTranslatedExpression;
                }

                // DateTime's second component is an int, but PostgreSQL's MAKE_TIMESTAMP accepts a double precision
                sqlArguments[5] = _sqlExpressionFactory.Convert(sqlArguments[5], typeof(double));

                return _sqlExpressionFactory.Function(
                    "make_timestamp", sqlArguments, nullable: true, TrueArrays[6], typeof(DateTime), _timestampMapping);
            }

            if (newExpression.Constructor == DateTimeCtor3 && newExpression.Arguments[6] is ConstantExpression { Value : DateTimeKind kind })
            {
                if (!TryTranslateArguments(out var sqlArguments))
                {
                    return QueryCompilationContext.NotTranslatedExpression;
                }

                // DateTime's second component is an int, but PostgreSQL's make_timestamp/make_timestamptz accepts a double precision.
                // Also chop off the last Kind argument which does not get sent to PostgreSQL
                var rewrittenArguments = new List<SqlExpression>
                {
                    sqlArguments[0], sqlArguments[1], sqlArguments[2], sqlArguments[3], sqlArguments[4],
                    _sqlExpressionFactory.Convert(sqlArguments[5], typeof(double))
                };

                if (kind == DateTimeKind.Utc)
                {
                    rewrittenArguments.Add(_sqlExpressionFactory.Constant("UTC"));
                }

                return kind == DateTimeKind.Utc
                    ? _sqlExpressionFactory.Function(
                        "make_timestamptz", rewrittenArguments, nullable: true, TrueArrays[8], typeof(DateTime), _timestampTzMapping)
                    : _sqlExpressionFactory.Function(
                        "make_timestamp", rewrittenArguments, nullable: true, TrueArrays[7], typeof(DateTime), _timestampMapping);
            }
        }

        // Translate new DateOnly(...) -> make_date
        if (newExpression.Constructor == DateOnlyCtor)
        {
            return TryTranslateArguments(out var sqlArguments)
                ? _sqlExpressionFactory.Function(
                    "make_date", sqlArguments, nullable: true, TrueArrays[3], typeof(DateOnly))
                : QueryCompilationContext.NotTranslatedExpression;
        }

        return QueryCompilationContext.NotTranslatedExpression;

        bool TryTranslateArguments(out SqlExpression[] sqlArguments)
        {
            sqlArguments = new SqlExpression[newExpression.Arguments.Count];
            for (var i = 0; i < sqlArguments.Length; i++)
            {
                var argument = newExpression.Arguments[i];
                if (TranslationFailed(argument, Visit(argument), out var sqlArgument))
                {
                    return false;
                }

                sqlArguments[i] = sqlArgument!;
            }

            return true;
        }
    }

    #region Copied from RelationalSqlTranslatingExpressionVisitor

    private static Expression TryRemoveImplicitConvert(Expression expression)
    {
        if (expression is UnaryExpression unaryExpression)
        {
            if (unaryExpression.NodeType == ExpressionType.Convert
                || unaryExpression.NodeType == ExpressionType.ConvertChecked)
            {
                var innerType = unaryExpression.Operand.Type.UnwrapNullableType();
                if (innerType.IsEnum)
                {
                    innerType = Enum.GetUnderlyingType(innerType);
                }
                var convertedType = unaryExpression.Type.UnwrapNullableType();

                if (innerType == convertedType
                    || (convertedType == typeof(int)
                        && (innerType == typeof(byte)
                            || innerType == typeof(sbyte)
                            || innerType == typeof(char)
                            || innerType == typeof(short)
                            || innerType == typeof(ushort))))
                {
                    return TryRemoveImplicitConvert(unaryExpression.Operand);
                }
            }
        }

        return expression;
    }


    [DebuggerStepThrough]
    private static bool TranslationFailed(Expression? original, Expression? translation, out SqlExpression? castTranslation)
    {
        if (original is not null && !(translation is SqlExpression))
        {
            castTranslation = null;
            return true;
        }

        castTranslation = translation as SqlExpression;
        return false;
    }

    #endregion Copied from RelationalSqlTranslatingExpressionVisitor
}