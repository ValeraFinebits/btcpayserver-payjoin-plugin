using Microsoft.EntityFrameworkCore;
using Npgsql;
using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PostgresPayjoinUniqueConstraintViolationDetector : IPayjoinUniqueConstraintViolationDetector
{
    public bool IsUniqueConstraintViolation(DbUpdateException exception, string constraintName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);

        return exception.InnerException is PostgresException postgresException &&
               postgresException.SqlState == PostgresErrorCodes.UniqueViolation &&
               string.Equals(postgresException.ConstraintName, constraintName, StringComparison.Ordinal);
    }
}
