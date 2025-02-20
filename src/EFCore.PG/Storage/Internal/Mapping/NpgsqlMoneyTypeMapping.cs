namespace Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.Mapping;

public class NpgsqlMoneyTypeMapping : DecimalTypeMapping
{
    public NpgsqlMoneyTypeMapping() : base("money", System.Data.DbType.Currency) {}

    protected NpgsqlMoneyTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new NpgsqlMoneyTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value)
        => base.GenerateNonNullSqlLiteral(value) + "::money";
}