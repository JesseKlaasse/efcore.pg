using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.Query.ExpressionTranslators.Internal;

public class NpgsqlMethodCallTranslatorProvider : RelationalMethodCallTranslatorProvider
{
    public virtual NpgsqlLTreeTranslator LTreeTranslator { get; }

    public NpgsqlMethodCallTranslatorProvider(
        RelationalMethodCallTranslatorProviderDependencies dependencies,
        IModel model,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies)
    {
        var sqlExpressionFactory = (NpgsqlSqlExpressionFactory)dependencies.SqlExpressionFactory;
        var typeMappingSource = (NpgsqlTypeMappingSource)dependencies.RelationalTypeMappingSource;
        var jsonTranslator = new NpgsqlJsonPocoTranslator(typeMappingSource, sqlExpressionFactory, model);
        LTreeTranslator = new NpgsqlLTreeTranslator(typeMappingSource, sqlExpressionFactory, model);

        AddTranslators(new IMethodCallTranslator[]
        {
            new NpgsqlArrayTranslator(sqlExpressionFactory, jsonTranslator, npgsqlSingletonOptions.UseRedshift),
            new NpgsqlByteArrayMethodTranslator(sqlExpressionFactory),
            new NpgsqlConvertTranslator(sqlExpressionFactory),
            new NpgsqlDateTimeMethodTranslator(typeMappingSource, sqlExpressionFactory),
            new NpgsqlFullTextSearchMethodTranslator(typeMappingSource, sqlExpressionFactory, model),
            new NpgsqlFuzzyStringMatchMethodTranslator(sqlExpressionFactory),
            new NpgsqlJsonDomTranslator(typeMappingSource, sqlExpressionFactory, model),
            new NpgsqlJsonDbFunctionsTranslator(typeMappingSource, sqlExpressionFactory, model),
            new NpgsqlLikeTranslator(sqlExpressionFactory),
            LTreeTranslator,
            new NpgsqlMathTranslator(typeMappingSource, sqlExpressionFactory, model),
            new NpgsqlNetworkTranslator(typeMappingSource, sqlExpressionFactory, model),
            new NpgsqlNewGuidTranslator(sqlExpressionFactory, npgsqlSingletonOptions),
            new NpgsqlObjectToStringTranslator(typeMappingSource, sqlExpressionFactory),
            new NpgsqlRandomTranslator(sqlExpressionFactory),
            new NpgsqlRangeTranslator(typeMappingSource, sqlExpressionFactory, model, npgsqlSingletonOptions),
            new NpgsqlRegexIsMatchTranslator(sqlExpressionFactory),
            new NpgsqlRowValueTranslator(sqlExpressionFactory),
            new NpgsqlStringMethodTranslator(typeMappingSource, sqlExpressionFactory, model),
            new NpgsqlTrigramsMethodTranslator(typeMappingSource, sqlExpressionFactory, model),
        });
    }
}