using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinUniqueConstraintViolationDetector
{
    bool IsUniqueConstraintViolation(DbUpdateException exception, string constraintName);
}
