using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BMS.Shared.Utilities;

/// <summary>
/// Canonical, shared implementation of the sale integrity hash formula.
///
/// ARCHITECTURAL RULE: Both the Register (CheckoutService) and the API
/// (SyncService) MUST use this class exclusively. Any divergence between the two
/// implementations causes every sale to fail the HashMismatch integrity check and
/// be permanently quarantined on the Register.
/// </summary>
public static class SaleIntegrityUtility
{
    /// <summary>
    /// Constructs the canonical hash input string for a sale.
    /// Formula: TotalAmount|CreatedAt|RegisterId|LocalSequence|SaleDataJson[|Split]
    ///
    /// All fields are formatted with InvariantCulture to guarantee byte-identical output
    /// regardless of the host machine's locale settings.
    /// </summary>
    public static string BuildHashInput(
        decimal totalAmount,
        DateTime createdAt,
        Guid registerId,
        long localSequence,
        string saleDataJson,
        bool isSplit)
    {
        var core = string.Concat(
            totalAmount.ToString("F2", CultureInfo.InvariantCulture), "|",
            createdAt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "|",
            registerId.ToString(), "|",
            localSequence.ToString(CultureInfo.InvariantCulture), "|",
            saleDataJson);

        return isSplit ? string.Concat(core, "|Split") : core;
    }

    /// <summary>
    /// Computes the SHA-256 hash of the given UTF-8 encoded input string.
    /// Returns lowercase hexadecimal (64 characters).
    /// </summary>
    public static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
